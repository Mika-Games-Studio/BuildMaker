using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.Secrets;

namespace UnityLocalCI.Core.State;

/// <summary>
/// Qualquer registro com status Running na inicializacao virou orfao por
/// reinicio da maquina. Marca como Interrupted e reenfileira o commit se ele
/// ainda for o HEAD da branch daquele projeto. A avaliacao e projeto a projeto.
/// </summary>
public sealed class OrphanRecovery
{
    private readonly IBuildStore _store;
    private readonly IGitClient _git;
    private readonly IBuildScheduler _scheduler;
    private readonly ICredentialStore _credentials;
    private readonly IClock _clock;
    private readonly ILogger<OrphanRecovery> _logger;

    public OrphanRecovery(
        IBuildStore store,
        IGitClient git,
        IBuildScheduler scheduler,
        ICredentialStore credentials,
        IClock clock,
        ILogger<OrphanRecovery> logger)
    {
        _store = store;
        _git = git;
        _scheduler = scheduler;
        _credentials = credentials;
        _clock = clock;
        _logger = logger;
    }

    public async Task RecoverAsync(IReadOnlyList<ResolvedProject> projects, CancellationToken ct)
    {
        var orphans = await _store.GetByStatusAsync(BuildStatus.Running, ct).ConfigureAwait(false);
        if (orphans.Count == 0) return;

        _logger.LogWarning(
            "{Count} build(s) estavam em execucao quando o servico parou. Marcando como Interrupted.",
            orphans.Count);

        var byProject = projects.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var orphan in orphans)
        {
            await _store.UpdateAsync(orphan with
            {
                Status = BuildStatus.Interrupted,
                FinishedAt = _clock.UtcNow,
                ErrorSummary = "Interrompida por reinicio do servico ou da maquina.",
            }, ct).ConfigureAwait(false);

            if (!byProject.TryGetValue(orphan.Project, out var project))
            {
                _logger.LogInformation(
                    "Build {BuildId} pertencia ao projeto {Project}, que nao esta mais habilitado; nao sera reenfileirada.",
                    orphan.Id, orphan.Project);
                continue;
            }

            await TryRequeueAsync(project, orphan, ct).ConfigureAwait(false);
        }
    }

    private async Task TryRequeueAsync(ResolvedProject project, BuildRecord orphan, CancellationToken ct)
    {
        var context = new GitContext
        {
            WorkspacePath = project.Repository.WorkspacePath,
            RepositoryUrl = project.Repository.Url,
            Branch = project.Repository.Branch,
            PersonalAccessToken = string.IsNullOrWhiteSpace(project.Repository.PatCredentialName)
                ? null
                : _credentials.Read(project.Repository.PatCredentialName!),
        };

        try
        {
            await _git.EnsureWorkspaceAsync(context, ct).ConfigureAwait(false);
            await _git.FetchAsync(context, ct).ConfigureAwait(false);
            var head = await _git.GetRemoteHeadShaAsync(context, ct).ConfigureAwait(false);

            if (!string.Equals(head, orphan.CommitSha, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[{Project}] build {BuildId} nao sera reenfileirada: {Sha} nao e mais o HEAD de {Branch}.",
                    project.Name, orphan.Id, orphan.ShortSha, project.Repository.Branch);
                return;
            }

            var commit = await _git.GetCommitInfoAsync(context, head, ct).ConfigureAwait(false);
            var buildId = await _scheduler
                .EnqueueAsync(project, commit, BuildTrigger.Recovery, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "[{Project}] build {BuildId} reenfileirada como {NewId}: {Sha} ainda e o HEAD.",
                project.Name, orphan.Id, buildId, orphan.ShortSha);
        }
        catch (GitCommandException ex)
        {
            // Nao vale derrubar o servico no boot por causa de um repositorio
            // fora do ar: o watcher vai reencontrar o commit no proximo poll.
            _logger.LogWarning(
                "[{Project}] nao foi possivel avaliar a recuperacao da build {BuildId}: {Message}",
                project.Name, orphan.Id, ex.Message);
        }
    }
}
