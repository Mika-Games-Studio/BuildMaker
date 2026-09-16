using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.State;
using UnityLocalCI.Core.Watching;
using Xunit;

namespace UnityLocalCI.Tests;

public class GitWatcherTests
{
    private const int DebounceSeconds = 120;

    private static (GitWatcher Watcher, FakeGitClient Git, RecordingScheduler Scheduler, FakeClock Clock, InMemoryBuildStore Store)
        Create(ResolvedProject? project = null)
    {
        var git = new FakeGitClient();
        var scheduler = new RecordingScheduler();
        var store = new InMemoryBuildStore();
        var clock = new FakeClock();

        var watcher = new GitWatcher(
            project ?? TestProjects.Create(debounceSeconds: DebounceSeconds),
            TimeSpan.Zero,
            git,
            scheduler,
            store,
            new FakeCredentialStore(),
            clock,
            NullLogger<GitWatcher>.Instance);

        return (watcher, git, scheduler, clock, store);
    }

    [Fact]
    public async Task Primeiro_poll_de_um_commit_novo_apenas_inicia_o_debounce()
    {
        var (watcher, git, scheduler, _, store) = Create();
        git.RemoteHeadSha = Sha('a');

        await watcher.PollOnceAsync(CancellationToken.None);

        Assert.Empty(scheduler.Enqueued);
        Assert.Equal(Sha('a'), await store.GetWatcherValueAsync("Crash", WatcherFields.LastSeenSha, default));
    }

    [Fact]
    public async Task Commit_enfileira_so_depois_da_janela_de_silencio()
    {
        var (watcher, git, scheduler, clock, _) = Create();
        git.RemoteHeadSha = Sha('a');

        await watcher.PollOnceAsync(CancellationToken.None);

        // Ainda dentro da janela.
        clock.Advance(TimeSpan.FromSeconds(DebounceSeconds - 1));
        await watcher.PollOnceAsync(CancellationToken.None);
        Assert.Empty(scheduler.Enqueued);

        clock.Advance(TimeSpan.FromSeconds(2));
        await watcher.PollOnceAsync(CancellationToken.None);
        Assert.Single(scheduler.Enqueued);
    }

    [Fact]
    public async Task Rajada_de_commits_produz_um_job_so_do_mais_recente()
    {
        var (watcher, git, scheduler, clock, _) = Create();

        // Tres merges em sequencia, cada um dentro da janela do anterior.
        foreach (var letter in new[] { 'a', 'b', 'c' })
        {
            git.RemoteHeadSha = Sha(letter);
            await watcher.PollOnceAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Empty(scheduler.Enqueued);

        // Silencio: a janela fecha sobre o ultimo commit.
        clock.Advance(TimeSpan.FromSeconds(DebounceSeconds));
        await watcher.PollOnceAsync(CancellationToken.None);

        var job = Assert.Single(scheduler.Enqueued);
        Assert.Equal(Sha('c'), job.Commit.Sha);
    }

    [Fact]
    public async Task Commit_novo_reinicia_a_janela_de_silencio()
    {
        var (watcher, git, scheduler, clock, _) = Create();

        git.RemoteHeadSha = Sha('a');
        await watcher.PollOnceAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(DebounceSeconds - 10));

        // Chega um commit novo quase no fim da janela: ela recomeca do zero.
        git.RemoteHeadSha = Sha('b');
        await watcher.PollOnceAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(20));
        await watcher.PollOnceAsync(CancellationToken.None);
        Assert.Empty(scheduler.Enqueued);

        clock.Advance(TimeSpan.FromSeconds(DebounceSeconds));
        await watcher.PollOnceAsync(CancellationToken.None);
        Assert.Single(scheduler.Enqueued);
    }

    [Fact]
    public async Task Mesmo_commit_nunca_e_construido_duas_vezes()
    {
        var (watcher, git, scheduler, clock, _) = Create();
        git.RemoteHeadSha = Sha('a');

        await watcher.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(DebounceSeconds + 1));
        await watcher.PollOnceAsync(CancellationToken.None);
        Assert.Single(scheduler.Enqueued);

        // Varios polls depois, sem commit novo: nada acontece.
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            await watcher.PollOnceAsync(CancellationToken.None);
        }

        Assert.Single(scheduler.Enqueued);
    }

    [Fact]
    public async Task Gatilho_manual_ignora_o_debounce()
    {
        var (watcher, git, scheduler, _, _) = Create();
        git.RemoteHeadSha = Sha('a');

        await watcher.EnqueueCurrentHeadAsync(BuildTrigger.Manual, CancellationToken.None);

        var job = Assert.Single(scheduler.Enqueued);
        Assert.Equal(BuildTrigger.Manual, job.Trigger);
    }

    private static string Sha(char letter) => new(letter, 40);
}
