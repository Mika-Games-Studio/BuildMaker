using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

public class BuildSchedulerTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly GatedBuildRunner _runner = new();
    private readonly InMemoryBuildStore _store = new();
    private readonly FakeSystemResources _resources = new();
    private readonly StubResourceGuard _guard = new();
    private readonly ServiceProvider _provider;
    private readonly BuildScheduler _scheduler;
    private readonly CiOptions _options;

    public BuildSchedulerTests()
    {
        _options = new CiOptions
        {
            Scheduler = new SchedulerOptions { MaxConcurrentBuilds = 2, MinFreeRamGb = 12, ResourceRecheckSeconds = 5 },
            Defaults = new ProjectDefaults
            {
                Unity = new UnityOptions { EditorVersion = "6000.0.47f1" },
                Publishing = new PublishingOptions { StagingFolder = @"C:\ci\staging" },
            },
            Projects =
            {
                NewProject("Crash"),
                NewProject("Mines"),
            },
        };

        var services = new ServiceCollection();
        services.AddSingleton<IBuildRunner>(_runner);
        _provider = services.BuildServiceProvider();

        _scheduler = new BuildScheduler(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _store,
            _guard,
            new RetentionService(_store, NullLogger<RetentionService>.Instance),
            _resources,
            new FakeClock(),
            Options.Create(_options),
            NullLogger<BuildScheduler>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _runner.ReleaseAll();
        await _scheduler.StopAsync(CancellationToken.None);
        _scheduler.Dispose();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Projetos_distintos_constroem_em_paralelo()
    {
        await _scheduler.StartAsync(CancellationToken.None);

        await _scheduler.EnqueueAsync(Project("Crash"), Commit('a'), BuildTrigger.Poll, default);
        await _scheduler.EnqueueAsync(Project("Mines"), Commit('b'), BuildTrigger.Poll, default);

        await WaitUntil(() => _runner.RunningCount == 2);

        Assert.Equal(2, _runner.MaxObservedConcurrency);
    }

    [Fact]
    public async Task Semaforo_global_respeita_max_concurrent_builds()
    {
        _options.Scheduler.MaxConcurrentBuilds = 1;

        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);

        try
        {
            await scheduler.EnqueueAsync(Project("Crash"), Commit('a'), BuildTrigger.Poll, default);
            await scheduler.EnqueueAsync(Project("Mines"), Commit('b'), BuildTrigger.Poll, default);

            await WaitUntil(() => _runner.RunningCount == 1);

            // O segundo projeto espera o primeiro terminar, em vez de falhar.
            await Task.Delay(150);
            Assert.Equal(1, _runner.RunningCount);
            Assert.Equal(1, _runner.MaxObservedConcurrency);

            _runner.ReleaseAll();
            await WaitUntil(() => _runner.CompletedCount == 2);
        }
        finally
        {
            _runner.ReleaseAll();
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task Fila_de_um_projeto_nao_bloqueia_a_de_outro()
    {
        _guard.BlockedProjects.Add("Crash");

        await _scheduler.StartAsync(CancellationToken.None);

        await _scheduler.EnqueueAsync(Project("Crash"), Commit('a'), BuildTrigger.Poll, default);
        await _scheduler.EnqueueAsync(Project("Mines"), Commit('b'), BuildTrigger.Poll, default);

        // Crash esta adiado por falta de recurso; Mines precisa rodar assim mesmo.
        await WaitUntil(() => _runner.StartedProjects.Contains("Mines"));
        Assert.DoesNotContain("Crash", _runner.StartedProjects);
    }

    [Fact]
    public async Task Falta_de_ram_adia_o_job_em_vez_de_descartar()
    {
        _guard.BlockedProjects.Add("Crash");

        await _scheduler.StartAsync(CancellationToken.None);
        await _scheduler.EnqueueAsync(Project("Crash"), Commit('a'), BuildTrigger.Poll, default);

        await Task.Delay(150);
        Assert.DoesNotContain("Crash", _runner.StartedProjects);

        // Recurso liberou: o job continua na fila e agora roda.
        _guard.BlockedProjects.Clear();
        await WaitUntil(() => _runner.StartedProjects.Contains("Crash"));
    }

    [Fact]
    public async Task Job_substituido_na_fila_e_marcado_como_cancelled()
    {
        _guard.BlockedProjects.Add("Crash");

        await _scheduler.StartAsync(CancellationToken.None);

        var first = await _scheduler.EnqueueAsync(Project("Crash"), Commit('a'), BuildTrigger.Poll, default);
        var second = await _scheduler.EnqueueAsync(Project("Crash"), Commit('b'), BuildTrigger.Poll, default);

        var replaced = await _store.GetAsync(first!.Value, default);
        var pending = await _store.GetAsync(second!.Value, default);

        Assert.Equal(BuildStatus.Cancelled, replaced!.Status);
        Assert.Equal(BuildStatus.Queued, pending!.Status);
    }

    [Fact]
    public void Teto_global_e_derivado_da_ram_instalada()
    {
        // 32 GB instalados dao 2; 16 GB dao 1; nunca menos de 1.
        Assert.Equal(2, SchedulerLimits.EffectiveMaxConcurrentBuilds(4, 32));
        Assert.Equal(1, SchedulerLimits.EffectiveMaxConcurrentBuilds(4, 16));
        Assert.Equal(1, SchedulerLimits.EffectiveMaxConcurrentBuilds(4, 8));
        Assert.Equal(1, SchedulerLimits.EffectiveMaxConcurrentBuilds(1, 128));
    }

    private BuildScheduler NewScheduler() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        _store,
        _guard,
        new RetentionService(_store, NullLogger<RetentionService>.Instance),
        _resources,
        new FakeClock(),
        Options.Create(_options),
        NullLogger<BuildScheduler>.Instance);

    private ResolvedProject Project(string name)
        => ProjectResolver.Resolve(_options.Projects.Single(p => p.Name == name), _options.Defaults);

    private static CommitInfo Commit(char letter)
        => new(new string(letter, 40), "Fulano de Tal", "mensagem");

    private static ProjectOptions NewProject(string name) => new()
    {
        Name = name,
        Enabled = true,
        Repository = new RepositoryOptions
        {
            Url = "https://example.invalid/" + name,
            Branch = "HML",
            WorkspacePath = $@"C:\ci\workspace\{name}",
        },
        Publishing = new PublishingOptions { ArtifactFolder = $@"C:\ci\artifacts\{name}" },
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("Condicao nao foi satisfeita dentro do tempo limite.");
    }
}

/// <summary>Executor que segura as builds ate o teste liberar, para medir concorrencia.</summary>
public sealed class GatedBuildRunner : IBuildRunner
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private int _running;

    private readonly List<string> _startedProjects = new();
    private int _maxObservedConcurrency;
    private int _completedCount;

    public int RunningCount { get { lock (_gate) return _running; } }
    public int MaxObservedConcurrency { get { lock (_gate) return _maxObservedConcurrency; } }
    public int CompletedCount { get { lock (_gate) return _completedCount; } }

    /// <summary>Copia sob lock: o teste le de outra thread enquanto as builds rodam.</summary>
    public IReadOnlyList<string> StartedProjects { get { lock (_gate) return _startedProjects.ToArray(); } }

    public void ReleaseAll() => _release.TrySetResult();

    public async Task RunAsync(BuildJob job, CancellationToken ct)
    {
        lock (_gate)
        {
            _running++;
            _maxObservedConcurrency = Math.Max(_maxObservedConcurrency, _running);
            _startedProjects.Add(job.Project.Name);
        }

        await _release.Task.WaitAsync(ct);

        lock (_gate)
        {
            _running--;
            _completedCount++;
        }
    }
}

public sealed class StubResourceGuard : IResourceGuard
{
    public HashSet<string> BlockedProjects { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ResourceCheck Check(ResolvedProject project)
        => BlockedProjects.Contains(project.Name)
            ? ResourceCheck.Blocked("RAM livre abaixo do minimo")
            : ResourceCheck.Ok;
}
