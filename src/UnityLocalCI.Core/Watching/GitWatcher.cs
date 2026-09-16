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
    private readonly IBuildScheduler _scheduler;
    private readonly IBuildStore _store;
    private readonly ICredentialStore _credentials;
    private readonly IClock _clock;
    private readonly ILogger<GitWatcher> _logger;
    private readonly TimeSpan _startupOffset;

    private DateTimeOffset? _debounceStartedAt;

    public GitWatcher(
        ResolvedProject project,
        TimeSpan startupOffset,
        IGitClient git,
        IBuildScheduler scheduler,
        IBuildStore store,
        ICredentialStore credentials,
        IClock clock,
        ILogger<GitWatcher> logger)
    {
        _project = project;
        _startupOffset = startupOffset;
        _git = git;
        _scheduler = scheduler;
        _store = store;
        _credentials = credentials;
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

            await _clock.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
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

    /// <summary>Enfileira o HEAD atual, ignorando o debounce. Usado pelo gatilho manual e pela recuperacao no boot.</summary>
    public async Task<long?> EnqueueCurrentHeadAsync(BuildTrigger trigger, CancellationToken ct)
    {
        var context = BuildGitContext();
        await _git.FetchAsync(context, ct).ConfigureAwait(false);
        var sha = await _git.GetRemoteHeadShaAsync(context, ct).ConfigureAwait(false);
        return await EnqueueAsync(context, sha, trigger, ct).ConfigureAwait(false);
    }

    private async Task<long?> EnqueueAsync(GitContext context, string sha, BuildTrigger trigger, CancellationToken ct)
    {
        var commit = await _git.GetCommitInfoAsync(context, sha, ct).ConfigureAwait(false);
        var buildId = await _scheduler.EnqueueAsync(_project, commit, trigger, ct).ConfigureAwait(false);

        if (buildId is not null)
        {
            // Marcado ja no enfileiramento: idempotencia e sobre nao construir o
            // mesmo commit duas vezes, inclusive apos reinicio da maquina.
            await _store.SetWatcherValueAsync(_project.Name, WatcherFields.LastBuiltSha, sha, ct).ConfigureAwait(false);
            _debounceStartedAt = null;
        }

        return buildId;
    }

    internal GitContext BuildGitContext() => new()
    {
        WorkspacePath = _project.Repository.WorkspacePath,
        RepositoryUrl = _project.Repository.Url,
        Branch = _project.Repository.Branch,
        PersonalAccessToken = string.IsNullOrWhiteSpace(_project.Repository.PatCredentialName)
            ? null
            : _credentials.Read(_project.Repository.PatCredentialName!),
    };

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;
}
