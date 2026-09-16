using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Notifications;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.Secrets;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Executa as etapas em sequencia. Falha de uma etapa interrompe a sequencia e
/// marca a build como Failed, exceto a publicacao, que tem fallback proprio.
/// </summary>
public sealed class BuildPipeline : IBuildRunner
{
    private readonly SyncStep _sync;
    private readonly UnityBuildStep _build;
    private readonly PackageStep _package;
    private readonly PublishStep _publish;
    private readonly IBuildStore _store;
    private readonly IBuildLogWriter _log;
    private readonly ICredentialStore _credentials;
    private readonly INotifier _notifier;
    private readonly IClock _clock;
    private readonly CiOptions _options;
    private readonly ILogger<BuildPipeline> _logger;

    public BuildPipeline(
        SyncStep sync,
        UnityBuildStep build,
        PackageStep package,
        PublishStep publish,
        IBuildStore store,
        IBuildLogWriter log,
        ICredentialStore credentials,
        INotifier notifier,
        IClock clock,
        IOptions<CiOptions> options,
        ILogger<BuildPipeline> logger)
    {
        _sync = sync;
        _build = build;
        _package = package;
        _publish = publish;
        _store = store;
        _log = log;
        _credentials = credentials;
        _notifier = notifier;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(BuildJob job, CancellationToken ct)
    {
        var startedAt = _clock.UtcNow;
        var logPath = Path.Combine(_options.State.LogFolder, $"build-{job.BuildId}.log");

        var context = new BuildContext
        {
            BuildId = job.BuildId,
            Project = job.Project,
            Commit = job.Commit,
            Trigger = job.Trigger,
            StartedAt = startedAt,
            LogPath = logPath,
            BuildOutputPath = Path.Combine(
                job.Project.Publishing.StagingFolder, "build", job.Project.Name),
            Git = BuildGitContext(job.Project),
        };

        _log.Open(logPath);
        _log.Write($"=== Build {job.BuildId} | {job.Project.Name} | {job.Commit.ShortSha} | {startedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss} ===");

        await MarkRunningAsync(job, startedAt, logPath, ct).ConfigureAwait(false);

        var steps = new IBuildStep[] { _sync, _build, _package, _publish };
        StepResult? failure = null;

        try
        {
            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();

                _log.Write($"--- {step.Name} ---");
                _logger.LogInformation("Etapa {Step} iniciada.", step.Name);

                var result = await step.ExecuteAsync(context, ct).ConfigureAwait(false);
                if (result.Success) continue;

                failure = result;
                _log.Write($"ETAPA {step.Name} FALHOU: {result.ErrorSummary}");
                break;
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(job, context, startedAt, BuildStatus.Interrupted,
                "Build interrompida pelo encerramento do servico.", ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            foreach (var warning in context.Warnings)
                _log.Write($"AVISO: {warning}");
        }

        var status = failure is null ? BuildStatus.Succeeded : BuildStatus.Failed;
        await FinishAsync(job, context, startedAt, status, failure?.ErrorSummary, ct).ConfigureAwait(false);
    }

    private GitContext BuildGitContext(ResolvedProject project) => new()
    {
        WorkspacePath = project.Repository.WorkspacePath,
        RepositoryUrl = project.Repository.Url,
        Branch = project.Repository.Branch,
        PersonalAccessToken = string.IsNullOrWhiteSpace(project.Repository.PatCredentialName)
            ? null
            : _credentials.Read(project.Repository.PatCredentialName!),
    };

    private async Task MarkRunningAsync(BuildJob job, DateTimeOffset startedAt, string logPath, CancellationToken ct)
    {
        var record = await _store.GetAsync(job.BuildId, ct).ConfigureAwait(false);
        if (record is null) return;

        await _store.UpdateAsync(record with
        {
            Status = BuildStatus.Running,
            StartedAt = startedAt,
            LogPath = logPath,
        }, ct).ConfigureAwait(false);
    }

    private async Task FinishAsync(
        BuildJob job,
        BuildContext context,
        DateTimeOffset startedAt,
        BuildStatus status,
        string? errorSummary,
        CancellationToken ct)
    {
        var finishedAt = _clock.UtcNow;
        var duration = (int)(finishedAt - startedAt).TotalSeconds;

        // Deliberadamente com CancellationToken.None: o registro do resultado
        // precisa acontecer mesmo quando a build foi interrompida.
        var record = await _store.GetAsync(job.BuildId, CancellationToken.None).ConfigureAwait(false);
        if (record is not null)
        {
            record = record with
            {
                Status = status,
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                DurationSeconds = duration,
                ArtifactPath = context.ArtifactPath,
                ArtifactSizeBytes = context.ArtifactSizeBytes,
                ArtifactSha256 = context.ArtifactSha256,
                PublishedPath = context.PublishedPath,
                PublishStatus = _publish.LastStatus,
                LogPath = context.LogPath,
                ErrorSummary = errorSummary,
            };

            await _store.UpdateAsync(record, CancellationToken.None).ConfigureAwait(false);
            await _notifier.NotifyAsync(record, context.Warnings, CancellationToken.None).ConfigureAwait(false);
        }

        _log.Write($"=== Resultado: {status} em {duration}s ===");
        _log.Dispose();

        if (status == BuildStatus.Succeeded)
            _logger.LogInformation("Build {BuildId} concluida em {Duration}s.", job.BuildId, duration);
        else
            _logger.LogError("Build {BuildId} terminou como {Status}: {Error}", job.BuildId, status, errorSummary);
    }
}
