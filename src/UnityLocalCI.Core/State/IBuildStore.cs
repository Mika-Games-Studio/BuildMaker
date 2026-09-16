namespace UnityLocalCI.Core.State;

public interface IBuildStore
{
    Task InitializeAsync(CancellationToken ct);

    Task<long> CreateAsync(BuildRecord record, CancellationToken ct);
    Task UpdateAsync(BuildRecord record, CancellationToken ct);
    Task<BuildRecord?> GetAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<BuildRecord>> GetByStatusAsync(BuildStatus status, CancellationToken ct);
    Task<IReadOnlyList<BuildRecord>> GetRecentAsync(string project, int count, CancellationToken ct);
    Task<BuildRecord?> GetLastFinishedAsync(string project, CancellationToken ct);

    Task<string?> GetWatcherValueAsync(string project, string field, CancellationToken ct);
    Task SetWatcherValueAsync(string project, string field, string? value, CancellationToken ct);
}

public static class WatcherFields
{
    public const string LastSeenSha = "last_seen_sha";
    public const string LastBuiltSha = "last_built_sha";
}
