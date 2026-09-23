using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Queue;

/// <summary>
/// Uma fila por projeto e um teto global de execucao. As duas coisas resolvem
/// problemas diferentes e precisam existir juntas: a fila impede dois Editores
/// no mesmo diretorio, que corromperiam a Library; o teto impede que dois builds
/// WebGL estourem a RAM da maquina, o que aconteceria venham eles de onde vierem.
/// </summary>
public sealed class BuildScheduler : BackgroundService, IBuildScheduler
{
    private readonly Dictionary<string, ProjectQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBuildStore _store;
    private readonly IResourceGuard _resourceGuard;
    private readonly IRetentionService _retention;
    private readonly IClock _clock;
    private readonly ILogger<BuildScheduler> _logger;
    private readonly SemaphoreSlim _globalSlots;
    private readonly int _maxConcurrentBuilds;
    private readonly TimeSpan _resourceRecheck;

    private readonly ConcurrentDictionary<long, CancellationTokenSource> _emAndamento = new();

    /// <summary>Builds que quem esta na janela mandou parar.</summary>
    private readonly ConcurrentDictionary<long, byte> _canceladas = new();

    private int _running;
    private volatile bool _pausada;

    public BuildScheduler(
        IServiceScopeFactory scopeFactory,
        IBuildStore store,
        IResourceGuard resourceGuard,
        IRetentionService retention,
        ISystemResources resources,
        IClock clock,
        IOptions<CiOptions> options,
        ILogger<BuildScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _store = store;
        _resourceGuard = resourceGuard;
        _retention = retention;
        _clock = clock;
        _logger = logger;

        var configuration = options.Value;

        _maxConcurrentBuilds = SchedulerLimits.EffectiveMaxConcurrentBuilds(
            configuration.Scheduler.MaxConcurrentBuilds,
            resources.InstalledPhysicalMemoryGb);

        _globalSlots = new SemaphoreSlim(_maxConcurrentBuilds, _maxConcurrentBuilds);
        _resourceRecheck = TimeSpan.FromSeconds(Math.Max(5, configuration.Scheduler.ResourceRecheckSeconds));

        foreach (var project in ProjectResolver.ResolveEnabled(configuration))
            _queues[project.Name] = new ProjectQueue(project.Name);

        _logger.LogInformation(
            "Scheduler com {Projects} projeto(s) e teto global de {Max} build(s) simultanea(s) " +
            "(configurado {Configured}, RAM instalada {Ram:0.0} GB).",
            _queues.Count, _maxConcurrentBuilds, configuration.Scheduler.MaxConcurrentBuilds,
            resources.InstalledPhysicalMemoryGb);
    }

    public SchedulerSnapshot Snapshot()
        => new(_queues.Values.Count(q => q.HasPending), Volatile.Read(ref _running), _maxConcurrentBuilds, _pausada);

    public async Task<long?> EnqueueAsync(
        ResolvedProject project, CommitInfo commit, BuildTrigger trigger, CancellationToken ct)
    {
        if (!_queues.TryGetValue(project.Name, out var queue))
        {
            _logger.LogWarning("Projeto {Project} nao esta registrado no scheduler; job ignorado.", project.Name);
            return null;
        }

        var queuedAt = _clock.UtcNow;
        var buildId = await _store.CreateAsync(new BuildRecord
        {
            CommitSha = commit.Sha,
            CommitMessage = commit.Message,
            CommitAuthor = commit.Author,
            Project = project.Name,
            Branch = project.Repository.Branch,
            Status = BuildStatus.Queued,
            QueuedAt = queuedAt,
        }, ct).ConfigureAwait(false);

        var job = new BuildJob
        {
            BuildId = buildId,
            Project = project,
            Commit = commit,
            Trigger = trigger,
            QueuedAt = queuedAt,
        };

        var replaced = queue.Enqueue(job);
        if (replaced is not null)
        {
            _logger.LogInformation(
                "[{Project}] build {Replaced} ({ReplacedSha}) foi substituida na fila por {BuildId} ({Sha}).",
                project.Name, replaced.BuildId, replaced.Commit.ShortSha, buildId, commit.ShortSha);

            await MarkCancelledAsync(replaced, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "[{Project}] build {BuildId} enfileirada para {Sha} ({Trigger}).",
            project.Name, buildId, commit.ShortSha, trigger);

        return buildId;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Um consumidor por projeto. Falha num deles nao alcanca os outros.
        var consumers = _queues.Values.Select(queue => ConsumeAsync(queue, stoppingToken));
        return Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(ProjectQueue queue, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.WaitForPendingAsync(stoppingToken).ConfigureAwait(false);
                await WaitForResumeAsync(stoppingToken).ConfigureAwait(false);
                await WaitForResourcesAsync(queue, stoppingToken).ConfigureAwait(false);

                await _globalSlots.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    // Retirar so agora garante que construimos o commit mais recente
                    // que chegou enquanto esperavamos vaga.
                    var job = queue.Take();
                    if (job is null) continue;

                    Interlocked.Increment(ref _running);
                    try
                    {
                        await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _running);
                    }
                }
                finally
                {
                    _globalSlots.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Isolamento de falha: o consumidor de um projeto nunca morre por
                // causa de um erro inesperado, senao aquele projeto para em silencio.
                _logger.LogError(ex, "[{Project}] erro inesperado no consumidor da fila.", queue.ProjectName);
                await _clock.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Espera ate haver recurso. Um job nunca e descartado por falta de RAM ou
    /// disco, apenas adiado, e o motivo da espera aparece no log.
    /// </summary>
    private async Task WaitForResourcesAsync(ProjectQueue queue, CancellationToken ct)
    {
        string? lastReason = null;

        while (!ct.IsCancellationRequested)
        {
            var project = queue.Peek()?.Project;
            if (project is null) return;

            var check = _resourceGuard.Check(project);
            if (check.CanStart)
            {
                if (lastReason is not null)
                    _logger.LogInformation("[{Project}] recurso liberado; build vai iniciar.", queue.ProjectName);
                return;
            }

            // Sem isto, um job adiado por falta de disco esperaria para sempre:
            // a retencao so roda ao fim de uma build, e nenhuma vai comecar.
            if (lastReason is null)
            {
                try
                {
                    await _retention.ApplyAsync(project, ct).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "[{Project}] retencao falhou ao tentar liberar espaco.", project.Name);
                }
            }

            if (check.Reason != lastReason)
            {
                _logger.LogWarning("[{Project}] build adiada: {Reason}.", queue.ProjectName, check.Reason);
                lastReason = check.Reason;
            }

            await _clock.Delay(_resourceRecheck, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Segura o consumidor enquanto a fila estiver pausada.
    ///
    /// A espera e ativa de meio em meio segundo, e nao por evento, de proposito:
    /// pausar e retomar acontecem uma vez por dia, e meio segundo de atraso ao
    /// retomar custa menos que um sinal a mais para manter certo.
    /// </summary>
    private async Task WaitForResumeAsync(CancellationToken ct)
    {
        var avisou = false;

        while (_pausada && !ct.IsCancellationRequested)
        {
            if (!avisou)
            {
                _logger.LogInformation("Fila pausada: nenhuma build nova comeca ate ser retomada.");
                avisou = true;
            }

            await _clock.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }

    public bool Paused
    {
        get => _pausada;
        set
        {
            if (_pausada == value) return;

            _pausada = value;
            _logger.LogInformation(value ? "Fila pausada." : "Fila retomada.");
        }
    }

    public bool Cancel(long buildId)
    {
        if (!_emAndamento.TryGetValue(buildId, out var cts)) return false;

        _canceladas[buildId] = 0;
        _logger.LogWarning("Build {BuildId} cancelada pela janela.", buildId);

        // Cancel lanca se o proprio source ja foi descartado — o que acontece
        // quando a build termina no meio deste caminho. Nao e erro: e a build
        // tendo ganhado a corrida.
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return false; }

        return true;
    }

    private async Task RunJobAsync(BuildJob job, CancellationToken stoppingToken)
    {
        // Escopo de DI e CancellationTokenSource proprios: timeout ou travamento
        // de um projeto nao pode alcancar os demais.
        using var scope = _scopeFactory.CreateScope();
        using var buildCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        _emAndamento[job.BuildId] = buildCts;

        var runner = scope.ServiceProvider.GetRequiredService<IBuildRunner>();

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["BuildId"] = job.BuildId,
            ["CommitSha"] = job.Commit.Sha,
            ["Project"] = job.Project.Name,
        }))
        {
            try
            {
                await runner.RunAsync(job, buildCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("[{Project}] build {BuildId} interrompida pelo encerramento do servico.",
                    job.Project.Name, job.BuildId);
                throw;
            }
            catch (OperationCanceledException) when (_canceladas.ContainsKey(job.BuildId))
            {
                // Cancelamento pedido na janela: o pipeline ja fechou o registro
                // como Interrompida, que e o que ele sabia na hora. Aqui a gente
                // sabe mais — e "cancelada" e a palavra honesta.
                await MarkCancelledByUserAsync(job, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Project}] build {BuildId} falhou de forma inesperada.",
                    job.Project.Name, job.BuildId);
            }
            finally
            {
                _emAndamento.TryRemove(job.BuildId, out _);
                _canceladas.TryRemove(job.BuildId, out _);
            }
        }
    }

    private async Task MarkCancelledByUserAsync(BuildJob job, CancellationToken ct)
    {
        var record = await _store.GetAsync(job.BuildId, ct).ConfigureAwait(false);
        if (record is null) return;

        await _store.UpdateAsync(record with
        {
            Status = BuildStatus.Cancelled,
            FinishedAt = record.FinishedAt ?? _clock.UtcNow,
            ErrorSummary = "Cancelada na janela.",
        }, ct).ConfigureAwait(false);
    }

    private async Task MarkCancelledAsync(BuildJob job, CancellationToken ct)
    {
        var record = await _store.GetAsync(job.BuildId, ct).ConfigureAwait(false);
        if (record is null) return;

        await _store.UpdateAsync(record with
        {
            Status = BuildStatus.Cancelled,
            FinishedAt = _clock.UtcNow,
            ErrorSummary = "Substituida na fila por um commit mais recente.",
        }, ct).ConfigureAwait(false);
    }
}
