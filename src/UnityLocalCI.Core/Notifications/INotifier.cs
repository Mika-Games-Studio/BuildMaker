using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Pipeline;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Notifications;

public interface INotifier
{
    Task NotifyAsync(BuildRecord build, IReadOnlyList<string> warnings, CancellationToken ct);
}

/// <summary>
/// Notificador de log, sempre ativo. Os arquivos de status na pasta de artefatos
/// e o Teams entram nas fases 2 e 3.
/// </summary>
public sealed class LogNotifier : INotifier
{
    private readonly ILogger<LogNotifier> _logger;

    public LogNotifier(ILogger<LogNotifier> logger) => _logger = logger;

    public Task NotifyAsync(BuildRecord build, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        var size = build.ArtifactSizeBytes is { } bytes ? PackageStep.FormatSize(bytes) : "-";

        _logger.LogInformation(
            "[{Project}] build {BuildId} {Status} | commit {Sha} por {Author} | {Duration}s | {Size} | {Path}",
            build.Project, build.Id, build.Status, build.ShortSha, build.CommitAuthor,
            build.DurationSeconds, size, build.PublishedPath ?? build.ArtifactPath ?? "-");

        foreach (var warning in warnings)
            _logger.LogWarning("[{Project}] {Warning}", build.Project, warning);

        return Task.CompletedTask;
    }
}
