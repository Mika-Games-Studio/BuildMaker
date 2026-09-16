using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Publishing;

public interface IPendingCopyService
{
    /// <summary>Retenta as copias pendentes de todos os projetos. Retorna quantas foram concluidas.</summary>
    Task<int> RetryAllAsync(CancellationToken ct);

    /// <summary>Retenta a copia de uma build especifica. Retorna a mensagem de erro, ou nulo em caso de sucesso.</summary>
    Task<string?> RetryAsync(long buildId, CancellationToken ct);
}

/// <summary>
/// Reenvio de artefato com copia pendente.
///
/// Quando o compartilhamento esta fora do ar, a build permanece bem-sucedida e o
/// zip fica no staging local marcado como PendingCopy. Este servico e quem
/// fecha o ciclo: sem ele, o artefato ficaria em disco local para sempre e
/// alguem teria de copiar na mao.
/// </summary>
public sealed class PendingCopyService : IPendingCopyService
{
    private readonly IBuildStore _store;
    private readonly ArtifactCopier _copier;
    private readonly IGlobalStatusWriter _globalStatus;
    private readonly IOptionsMonitor<CiOptions> _options;
    private readonly ILogger<PendingCopyService> _logger;

    public PendingCopyService(
        IBuildStore store,
        ArtifactCopier copier,
        IGlobalStatusWriter globalStatus,
        IOptionsMonitor<CiOptions> options,
        ILogger<PendingCopyService> logger)
    {
        _store = store;
        _copier = copier;
        _globalStatus = globalStatus;
        _options = options;
        _logger = logger;
    }

    public async Task<int> RetryAllAsync(CancellationToken ct)
    {
        var pending = await FindPendingAsync(ct).ConfigureAwait(false);
        if (pending.Count == 0) return 0;

        _logger.LogInformation("{Count} artefato(s) com copia pendente; tentando reenviar.", pending.Count);

        var sent = 0;
        foreach (var build in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (await TryCopyAsync(build, ct).ConfigureAwait(false) is null) sent++;
        }

        if (sent > 0) await _globalStatus.WriteAsync(ct).ConfigureAwait(false);
        return sent;
    }

    public async Task<string?> RetryAsync(long buildId, CancellationToken ct)
    {
        var build = await _store.GetAsync(buildId, ct).ConfigureAwait(false);
        if (build is null) return $"build {buildId} nao existe";

        if (build.PublishStatus == PublishStatus.Published)
            return $"build {buildId} ja esta publicada em {build.PublishedPath}";

        var error = await TryCopyAsync(build, ct).ConfigureAwait(false);
        if (error is null) await _globalStatus.WriteAsync(ct).ConfigureAwait(false);

        return error;
    }

    private async Task<IReadOnlyList<BuildRecord>> FindPendingAsync(CancellationToken ct)
    {
        var pending = new List<BuildRecord>();

        foreach (var project in ProjectResolver.ResolveEnabled(_options.CurrentValue))
        {
            var recent = await _store.GetRecentAsync(project.Name, 100, ct).ConfigureAwait(false);
            pending.AddRange(recent.Where(b => b.PublishStatus == PublishStatus.PendingCopy));
        }

        // Da mais antiga para a mais nova: se o destino voltou, a ordem em que
        // os zips aparecem la e a ordem em que foram construidos.
        return pending.OrderBy(b => b.Id).ToList();
    }

    private async Task<string?> TryCopyAsync(BuildRecord build, CancellationToken ct)
    {
        var project = ProjectResolver
            .ResolveEnabled(_options.CurrentValue)
            .FirstOrDefault(p => string.Equals(p.Name, build.Project, StringComparison.OrdinalIgnoreCase));

        if (project is null)
            return $"projeto {build.Project} nao esta mais habilitado na configuracao";

        if (build.ArtifactPath is not { } staged)
            return $"build {build.Id} nao tem artefato em staging para reenviar";

        var outcome = await _copier
            .CopyAsync(staged, project.Publishing.ArtifactFolder, build.ArtifactSha256, ct)
            .ConfigureAwait(false);

        if (!outcome.Success)
        {
            _logger.LogWarning(
                "[{Project}] copia da build {BuildId} continua pendente: {Error}",
                build.Project, build.Id, outcome.Error);

            return outcome.Error;
        }

        await _store.UpdateAsync(build with
        {
            PublishedPath = outcome.Location,
            PublishStatus = PublishStatus.Published,
        }, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "[{Project}] artefato pendente da build {BuildId} foi reenviado para {Path}.",
            build.Project, build.Id, outcome.Location);

        return null;
    }
}

/// <summary>
/// Tenta reenviar as copias pendentes na inicializacao e periodicamente. A
/// maquina que caiu durante a noite reencontra o compartilhamento sozinha, sem
/// ninguem precisar lembrar.
/// </summary>
public sealed class PendingCopyRetryWorker : BackgroundService
{
    private readonly IPendingCopyService _pending;
    private readonly IClock _clock;
    private readonly ILogger<PendingCopyRetryWorker> _logger;
    private readonly TimeSpan _interval;

    public PendingCopyRetryWorker(
        IPendingCopyService pending,
        IClock clock,
        IOptions<CiOptions> options,
        ILogger<PendingCopyRetryWorker> logger)
    {
        _pending = pending;
        _clock = clock;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.Scheduler.PendingCopyRetryMinutes));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _pending.RetryAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Erro inesperado ao reenviar copias pendentes.");
            }

            await _clock.Delay(_interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
