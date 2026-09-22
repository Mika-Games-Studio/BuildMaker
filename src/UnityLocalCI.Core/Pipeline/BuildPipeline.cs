using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Notifications;
using UnityLocalCI.Core.Publishing;
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
    private readonly IReadOnlyList<INotifier> _notifiers;
    private readonly IGlobalStatusWriter _globalStatus;
    private readonly IRetentionService _retention;
    private readonly IClock _clock;
    private readonly BuildProgress _progress;
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
        IEnumerable<INotifier> notifiers,
        IGlobalStatusWriter globalStatus,
        IRetentionService retention,
        IClock clock,
        BuildProgress progress,
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
        _notifiers = notifiers.ToList();
        _globalStatus = globalStatus;
        _retention = retention;
        _clock = clock;
        _progress = progress;
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
                _progress.Entrou(job.BuildId, step.Name);

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

        // O _STATUS-GERAL.txt e reescrito tambem aqui, e nao so no fim: quem
        // abre o arquivo durante uma build de 30 minutos precisa ver
        // "construindo", nao o resultado da build anterior.
        await _globalStatus.WriteAsync(ct).ConfigureAwait(false);
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

        // Antes de qualquer outra coisa: daqui para a frente a build nao esta
        // mais em etapa nenhuma, e um letreiro parado numa etapa que ja passou
        // mente mais do que letreiro nenhum.
        _progress.Saiu(job.BuildId);

        // O log e fechado antes dos notificadores porque um deles copia o arquivo
        // para a pasta de destino, e a copia precisa incluir a linha de resultado.
        _log.Write($"=== Resultado: {status} em {duration}s ===");
        _log.Dispose();

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

            foreach (var notifier in _notifiers)
            {
                try
                {
                    await notifier.NotifyAsync(record, context.Warnings, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // Um notificador quebrado nunca pode mudar o resultado de uma
                    // build que ja terminou, nem impedir os outros de rodarem.
                    _logger.LogError(exception, "Notificador {Notifier} falhou.", notifier.GetType().Name);
                }
            }

            await _globalStatus.WriteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            // Retencao depois dos notificadores: o _HISTORICO.txt e reescrito
            // antes da poda, e quem for podado ja aparece nele sem o zip.
            await _retention.ApplyAsync(job.Project, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Retencao falhou; nada foi removido.");
        }

        if (status == BuildStatus.Succeeded)
            _logger.LogInformation("Build {BuildId} concluida em {Duration}s.", job.BuildId, duration);
        else
            _logger.LogError("Build {BuildId} terminou como {Status}: {Error}", job.BuildId, status, errorSummary);
    }
}
