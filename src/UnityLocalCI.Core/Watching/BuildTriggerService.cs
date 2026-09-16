using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.Secrets;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Watching;

/// <summary>
/// Enfileiramento compartilhado entre o polling e o gatilho manual.
///
/// Existe para que a regra de idempotencia — gravar o last_built_sha no momento
/// do enfileiramento, para o mesmo commit nunca ser construido duas vezes — viva
/// num lugar so. Duplicada, ela divergiria na primeira mudanca.
/// </summary>
public sealed class BuildTriggerService
{
    private readonly IGitClient _git;
    private readonly IBuildScheduler _scheduler;
    private readonly IBuildStore _store;
    private readonly ICredentialStore _credentials;
    private readonly ILogger<BuildTriggerService> _logger;

    public BuildTriggerService(
        IGitClient git,
        IBuildScheduler scheduler,
        IBuildStore store,
        ICredentialStore credentials,
        ILogger<BuildTriggerService> logger)
    {
        _git = git;
        _scheduler = scheduler;
        _store = store;
        _credentials = credentials;
        _logger = logger;
    }

    /// <summary>O PAT e lido a cada invocacao e nunca persistido em lugar nenhum.</summary>
    public GitContext CreateContext(ResolvedProject project) => new()
    {
        WorkspacePath = project.Repository.WorkspacePath,
        RepositoryUrl = project.Repository.Url,
        Branch = project.Repository.Branch,
        PersonalAccessToken = string.IsNullOrWhiteSpace(project.Repository.PatCredentialName)
            ? null
            : _credentials.Read(project.Repository.PatCredentialName!),
    };

    public async Task<long?> EnqueueAsync(
        ResolvedProject project, GitContext context, string sha, BuildTrigger trigger, CancellationToken ct)
    {
        var commit = await _git.GetCommitInfoAsync(context, sha, ct).ConfigureAwait(false);
        var buildId = await _scheduler.EnqueueAsync(project, commit, trigger, ct).ConfigureAwait(false);

        if (buildId is not null)
        {
            await _store
                .SetWatcherValueAsync(project.Name, WatcherFields.LastBuiltSha, sha, ct)
                .ConfigureAwait(false);
        }

        return buildId;
    }

    /// <summary>
    /// Enfileira o HEAD atual, ignorando o debounce. Usado pelo gatilho manual e
    /// pela recuperacao de build interrompida.
    /// </summary>
    public async Task<long?> EnqueueHeadAsync(ResolvedProject project, BuildTrigger trigger, CancellationToken ct)
    {
        var context = CreateContext(project);

        await _git.EnsureWorkspaceAsync(context, ct).ConfigureAwait(false);
        await _git.FetchAsync(context, ct).ConfigureAwait(false);

        var sha = await _git.GetRemoteHeadShaAsync(context, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sha))
        {
            _logger.LogWarning("[{Project}] nao foi possivel determinar o HEAD de {Branch}.",
                project.Name, project.Repository.Branch);
            return null;
        }

        return await EnqueueAsync(project, context, sha, trigger, ct).ConfigureAwait(false);
    }
}
