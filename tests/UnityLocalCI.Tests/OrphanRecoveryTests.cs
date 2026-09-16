using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

public class OrphanRecoveryTests
{
    private readonly InMemoryBuildStore _store = new();
    private readonly FakeGitClient _git = new();
    private readonly RecordingScheduler _scheduler = new();

    private OrphanRecovery Create() => new(
        _store, _git, _scheduler, new FakeCredentialStore(),
        new FakeClock(), NullLogger<OrphanRecovery>.Instance);

    private async Task<long> SeedRunningBuildAsync(string project, string sha)
        => await _store.CreateAsync(new BuildRecord
        {
            CommitSha = sha,
            Project = project,
            Branch = "HML",
            Status = BuildStatus.Running,
            QueuedAt = DateTimeOffset.UnixEpoch,
            StartedAt = DateTimeOffset.UnixEpoch,
        }, default);

    [Fact]
    public async Task Build_orfa_vira_interrupted()
    {
        var id = await SeedRunningBuildAsync("Crash", Sha('a'));
        _git.RemoteHeadSha = Sha('b');

        await Create().RecoverAsync(new[] { TestProjects.Create() }, default);

        var record = await _store.GetAsync(id, default);
        Assert.Equal(BuildStatus.Interrupted, record!.Status);
        Assert.NotNull(record.FinishedAt);
    }

    [Fact]
    public async Task Commit_ainda_no_head_volta_para_a_fila()
    {
        await SeedRunningBuildAsync("Crash", Sha('a'));
        _git.RemoteHeadSha = Sha('a');

        await Create().RecoverAsync(new[] { TestProjects.Create() }, default);

        var job = Assert.Single(_scheduler.Enqueued);
        Assert.Equal(Sha('a'), job.Commit.Sha);
        Assert.Equal(BuildTrigger.Recovery, job.Trigger);
    }

    [Fact]
    public async Task Commit_ultrapassado_nao_volta_para_a_fila()
    {
        await SeedRunningBuildAsync("Crash", Sha('a'));
        _git.RemoteHeadSha = Sha('b');

        await Create().RecoverAsync(new[] { TestProjects.Create() }, default);

        Assert.Empty(_scheduler.Enqueued);
    }

    [Fact]
    public async Task Recuperacao_e_avaliada_projeto_a_projeto()
    {
        await SeedRunningBuildAsync("Crash", Sha('a'));
        await SeedRunningBuildAsync("Desativado", Sha('a'));
        _git.RemoteHeadSha = Sha('a');

        // So Crash segue habilitado na configuracao.
        await Create().RecoverAsync(new[] { TestProjects.Create() }, default);

        var job = Assert.Single(_scheduler.Enqueued);
        Assert.Equal("Crash", job.Project.Name);

        // Ainda assim, a orfa do projeto desativado precisa sair de Running.
        var orphans = await _store.GetByStatusAsync(BuildStatus.Running, default);
        Assert.Empty(orphans);
    }

    [Fact]
    public async Task Sem_orfas_nada_acontece()
    {
        await Create().RecoverAsync(new[] { TestProjects.Create() }, default);
        Assert.Empty(_scheduler.Enqueued);
    }

    private static string Sha(char letter) => new(letter, 40);
}
