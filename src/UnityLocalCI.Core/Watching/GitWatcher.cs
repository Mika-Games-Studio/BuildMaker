using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.Secrets;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Watching;

/// <summary>
/// Uma instancia por projeto habilitado. As instancias sao independentes: erro
/// de rede ou repositorio indisponivel em um projeto nao interrompe os outros.
///
/// O debounce e o passo 5 do laco: qualquer commit novo reinicia a janela, entao
/// uma rajada de merges produz uma unica build, do ultimo commit.
/// </summary>
public sealed class GitWatcher : BackgroundService
{
    private readonly ResolvedProject _project;
    private readonly IGitClient _git;
    private readonly IBuildStore _store;
    private readonly BuildTriggerService _trigger;
    private readonly IClock _clock;
    private readonly ILogger<GitWatcher> _logger;
    private readonly TimeSpan _startupOffset;

    private DateTimeOffset? _debounceStartedAt;

    /// <summary>Sinal do hook post-merge: antecipa a proxima verificacao.</summary>
    private readonly SemaphoreSlim _poke = new(0, 1);

    public GitWatcher(
        ResolvedProject project,
        TimeSpan startupOffset,
        IGitClient git,
        IBuildStore store,
        BuildTriggerService trigger,
        IClock clock,
        ILogger<GitWatcher> logger)
    {
        _project = project;
        _startupOffset = startupOffset;
        _git = git;
        _store = store;
        _trigger = trigger;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Espacamento na inicializacao para que N projetos nao disparem
        // 'git fetch' no mesmo instante.
        if (_startupOffset > TimeSpan.Zero)
            await _clock.Delay(_startupOffset, stoppingToken).ConfigureAwait(false);

        var interval = TimeSpan.FromSeconds(_project.Watcher.PollIntervalSeconds);

        _logger.LogInformation(
            "[{Project}] observando {Branch} a cada {Interval}s, debounce de {Debounce}s.",
            _project.Name, _project.Repository.Branch,
            _project.Watcher.PollIntervalSeconds, _project.Watcher.DebounceSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (GitCommandException ex)
            {
                // Repositorio fora do ar e uma condicao esperada: loga e tenta de novo.
                _logger.LogWarning("[{Project}] falha ao consultar o repositorio: {Message}", _project.Name, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Project}] erro inesperado no watcher.", _project.Name);
            }

            // O que vier primeiro: o intervalo de polling ou um sinal do hook.
            // O hook e so otimizacao de latencia e pode falhar sem consequencia,
            // porque o polling continua sendo a fonte da verdade.
            await Task.WhenAny(
                _clock.Delay(interval, stoppingToken),
                _poke.WaitAsync(stoppingToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Antecipa a proxima verificacao. Nao enfileira nada: quem decide continua
    /// sendo o laco de polling, com o debounce e o ultimo sha que ele conhece.
    /// </summary>
    public void PokeNow()
    {
        try { _poke.Release(); } catch (SemaphoreFullException) { /* ja sinalizado */ }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var context = BuildGitContext();

        await _git.EnsureWorkspaceAsync(context, ct).ConfigureAwait(false);
        await _git.FetchAsync(context, ct).ConfigureAwait(false);

        var sha = await _git.GetRemoteHeadShaAsync(context, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sha)) return;

        var lastBuilt = await _store
            .GetWatcherValueAsync(_project.Name, WatcherFields.LastBuiltSha, ct).ConfigureAwait(false);

        if (string.Equals(sha, lastBuilt, StringComparison.OrdinalIgnoreCase))
        {
            _debounceStartedAt = null;
            return;
        }

        var lastSeen = await _store
            .GetWatcherValueAsync(_project.Name, WatcherFields.LastSeenSha, ct).ConfigureAwait(false);

        if (!string.Equals(sha, lastSeen, StringComparison.OrdinalIgnoreCase))
        {
            // Commit novo: grava e reinicia a janela de silencio.
            await _store.SetWatcherValueAsync(_project.Name, WatcherFields.LastSeenSha, sha, ct).ConfigureAwait(false);
            _debounceStartedAt = _clock.UtcNow;

            _logger.LogInformation(
                "[{Project}] commit novo {Sha}; aguardando {Debounce}s de silencio antes de enfileirar.",
                _project.Name, Short(sha), _project.Watcher.DebounceSeconds);
            return;
        }

        // Mesmo SHA da ultima observacao: so enfileira quando a janela fechar.
        _debounceStartedAt ??= _clock.UtcNow;

        var elapsed = _clock.UtcNow - _debounceStartedAt.Value;
        if (elapsed < TimeSpan.FromSeconds(_project.Watcher.DebounceSeconds)) return;

        await EnqueueAsync(context, sha, BuildTrigger.Poll, ct).ConfigureAwait(false);
    }

    /// <summary>Enfileira o HEAD atual, ignorando o debounce.</summary>
    public Task<long?> EnqueueCurrentHeadAsync(BuildTrigger trigger, CancellationToken ct)
        => _trigger.EnqueueHeadAsync(_project, trigger, ct);

    private async Task<long?> EnqueueAsync(GitContext context, string sha, BuildTrigger trigger, CancellationToken ct)
    {
        // O last_built_sha e gravado dentro do servico, junto com o
        // enfileiramento; aqui so fechamos a janela de debounce.
        var buildId = await _trigger.EnqueueAsync(_project, context, sha, trigger, ct).ConfigureAwait(false);
        if (buildId is not null) _debounceStartedAt = null;
        return buildId;
    }

    internal GitContext BuildGitContext() => _trigger.CreateContext(_project);

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;
}
