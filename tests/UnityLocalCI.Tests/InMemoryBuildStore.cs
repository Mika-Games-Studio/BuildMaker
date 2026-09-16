using UnityLocalCI.Core.State;

namespace UnityLocalCI.Tests;

public sealed class InMemoryBuildStore : IBuildStore
{
    private readonly object _gate = new();
    private readonly Dictionary<long, BuildRecord> _builds = new();
    private readonly Dictionary<string, string?> _watcher = new(StringComparer.Ordinal);
    private long _nextId = 1;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<long> CreateAsync(BuildRecord record, CancellationToken ct)
    {
        lock (_gate)
        {
            var id = _nextId++;
            _builds[id] = record with { Id = id };
            return Task.FromResult(id);
        }
    }

    public Task UpdateAsync(BuildRecord record, CancellationToken ct)
    {
        lock (_gate) _builds[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<BuildRecord?> GetAsync(long id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_builds.TryGetValue(id, out var r) ? r : null);
    }

    public Task<IReadOnlyList<BuildRecord>> GetByStatusAsync(BuildStatus status, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<BuildRecord> result = _builds.Values
                .Where(b => b.Status == status).OrderBy(b => b.Id).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<BuildRecord>> GetRecentAsync(string project, int count, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<BuildRecord> result = _builds.Values
                .Where(b => b.Project == project).OrderByDescending(b => b.Id).Take(count).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<BuildRecord?> GetLastFinishedAsync(string project, CancellationToken ct)
    {
        lock (_gate)
        {
            var result = _builds.Values
                .Where(b => b.Project == project && b.FinishedAt is not null)
                .OrderByDescending(b => b.Id).FirstOrDefault();
            return Task.FromResult(result);
        }
    }

    public Task<string?> GetWatcherValueAsync(string project, string field, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_watcher.TryGetValue($"{project}:{field}", out var v) ? v : null);
    }

    public Task SetWatcherValueAsync(string project, string field, string? value, CancellationToken ct)
    {
        lock (_gate) _watcher[$"{project}:{field}"] = value;
        return Task.CompletedTask;
    }
}
