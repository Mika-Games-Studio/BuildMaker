using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.Secrets;

namespace UnityLocalCI.Tests;

/// <summary>Relogio controlado pelo teste. Nenhum teste depende de tempo real.</summary>
public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? start = null)
        => UtcNow = start ?? new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; private set; }
    public DateTimeOffset Now => UtcNow.ToLocalTime();

    public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);

    /// <summary>
    /// Avanca o relogio e cede a vez, sem dormir o tempo pedido: nenhum teste
    /// pode depender de tempo real para nao ficar lento nem intermitente.
    /// </summary>
    public Task Delay(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Advance(delay);
        return Task.Delay(1, ct);
    }
}

public sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, string value) => _values[name] = value;
    public bool Exists(string credentialName) => _values.ContainsKey(credentialName);
    public string? Read(string credentialName) => _values.TryGetValue(credentialName, out var v) ? v : null;
    public void Write(string credentialName, string secret) => _values[credentialName] = secret;
}

public sealed class FakeSystemResources : ISystemResources
{
    public double FreePhysicalMemoryGb { get; set; } = 32;
    public double InstalledPhysicalMemoryGb { get; set; } = 32;
    public double FreeDiskGbValue { get; set; } = 500;

    public double FreeDiskGb(string path) => FreeDiskGbValue;
}

/// <summary>Executor de processos que grava as invocacoes em vez de rodar nada.</summary>
public sealed class RecordingProcessRunner : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = new();
    public Func<ProcessRequest, ProcessResult> Respond { get; set; } =
        _ => new ProcessResult(0, false, "", "");

    public Task<ProcessResult> RunAsync(
        ProcessRequest request, Action<OutputStream, string>? onOutput, CancellationToken ct)
    {
        Requests.Add(request);
        var result = Respond(request);

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            onOutput?.Invoke(OutputStream.StandardOutput, line.TrimEnd('\r'));

        return Task.FromResult(result);
    }
}

public sealed class FakeGitClient : IGitClient
{
    public string RemoteHeadSha { get; set; } = new string('a', 40);
    public CommitInfo Commit { get; set; } = new(new string('a', 40), "Fulano de Tal", "commit de teste");
    public bool WorkspaceExists { get; set; } = true;
    public int FetchCount { get; private set; }
    public List<string> CheckedOut { get; } = new();

    public Task<SyncOutcome> EnsureWorkspaceAsync(GitContext context, CancellationToken ct)
        => Task.FromResult(new SyncOutcome(!WorkspaceExists));

    public Task FetchAsync(GitContext context, CancellationToken ct)
    {
        FetchCount++;
        return Task.CompletedTask;
    }

    public Task<string> GetRemoteHeadShaAsync(GitContext context, CancellationToken ct)
        => Task.FromResult(RemoteHeadSha);

    public Task<CommitInfo> GetCommitInfoAsync(GitContext context, string sha, CancellationToken ct)
        => Task.FromResult(Commit with { Sha = sha });

    public Task CheckoutAsync(GitContext context, string sha, CancellationToken ct)
    {
        CheckedOut.Add(sha);
        return Task.CompletedTask;
    }

    public List<string> RemoteBranches { get; } = ["HML", "main"];

    public Task<IReadOnlyList<string>> ListRemoteBranchesAsync(GitContext context, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(RemoteBranches);
}

public sealed class RecordingScheduler : IBuildScheduler
{
    private long _nextId = 1;

    public List<(ResolvedProject Project, CommitInfo Commit, BuildTrigger Trigger)> Enqueued { get; } = new();

    public Task<long?> EnqueueAsync(
        ResolvedProject project, CommitInfo commit, BuildTrigger trigger, CancellationToken ct)
    {
        Enqueued.Add((project, commit, trigger));
        return Task.FromResult<long?>(_nextId++);
    }

    public SchedulerSnapshot Snapshot() => new(0, 0, 1);
}

public static class TestProjects
{
    public static ResolvedProject Create(
        string name = "Crash",
        string workspace = @"C:\ci\workspace\crash",
        string staging = @"C:\ci\staging",
        string artifactFolder = @"C:\ci\artifacts\crash",
        int debounceSeconds = 120,
        int pollSeconds = 60,
        int minFreeDiskGb = 50)
        => new(
            Name: name,
            Repository: new RepositoryOptions
            {
                Url = "https://example.invalid/repo",
                Branch = "HML",
                WorkspacePath = workspace,
                PatCredentialName = null,
            },
            ManualTriggerFile: null,
            Watcher: new ResolvedWatcher(pollSeconds, debounceSeconds, 8081),
            Unity: new ResolvedUnity("6000.0.47f1", "WebGL", "Builder.PerformBuild", 90, Array.Empty<string>()),
            Packaging: new ResolvedPackaging("{project}-{branch}-{date}-{sha}.zip", true),
            Publishing: new ResolvedPublishing(staging, artifactFolder, true, true),
            Retention: new ResolvedRetention(10, minFreeDiskGb));
}

/// <summary>
/// Cliente de git que so registra o que foi pedido. Serve para provar de qual
/// credencial a tela de configuracao se serve, sem tocar em rede.
/// </summary>
public sealed class CapturingGitClient : IGitClient
{
    public ManualResetEventSlim Chamado { get; } = new(false);
    public GitContext? UltimoContexto { get; private set; }
    public List<string> Branches { get; } = ["main"];

    public Task<SyncOutcome> EnsureWorkspaceAsync(GitContext context, CancellationToken ct)
        => Task.FromResult(new SyncOutcome(false));

    public Task FetchAsync(GitContext context, CancellationToken ct) => Task.CompletedTask;

    public Task<string> GetRemoteHeadShaAsync(GitContext context, CancellationToken ct)
        => Task.FromResult(new string('a', 40));

    public Task<CommitInfo> GetCommitInfoAsync(GitContext context, string sha, CancellationToken ct)
        => Task.FromResult(new CommitInfo(sha, "autor", "mensagem"));

    public Task CheckoutAsync(GitContext context, string sha, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListRemoteBranchesAsync(GitContext context, CancellationToken ct)
    {
        UltimoContexto = context;
        Chamado.Set();
        return Task.FromResult<IReadOnlyList<string>>(Branches.ToArray());
    }
}
