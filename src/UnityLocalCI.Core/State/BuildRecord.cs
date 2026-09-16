namespace UnityLocalCI.Core.State;

public enum BuildStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted,
}

public enum PublishStatus
{
    None,
    Published,
    PendingCopy,
    Failed,
}

public sealed record BuildRecord
{
    public long Id { get; init; }
    public required string CommitSha { get; init; }
    public string? CommitMessage { get; init; }
    public string? CommitAuthor { get; init; }
    public required string Project { get; init; }
    public required string Branch { get; init; }
    public BuildStatus Status { get; init; } = BuildStatus.Queued;
    public DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int? DurationSeconds { get; init; }
    public string? ArtifactPath { get; init; }
    public long? ArtifactSizeBytes { get; init; }
    public string? ArtifactSha256 { get; init; }
    public string? PublishedPath { get; init; }
    public PublishStatus PublishStatus { get; init; } = PublishStatus.None;
    public string? LogPath { get; init; }
    public string? ErrorSummary { get; init; }

    public string ShortSha => CommitSha.Length >= 7 ? CommitSha[..7] : CommitSha;
}
