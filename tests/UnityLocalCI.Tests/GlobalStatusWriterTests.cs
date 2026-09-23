using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

public class GlobalStatusWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "unitylocalci-tests", Guid.NewGuid().ToString("N"));

    private readonly string _statusFile;
    private readonly InMemoryBuildStore _store = new();
    private readonly CiOptions _options;

    public GlobalStatusWriterTests()
    {
        Directory.CreateDirectory(_root);
        _statusFile = Path.Combine(_root, "_STATUS-GERAL.txt");

        _options = new CiOptions
        {
            Scheduler = new SchedulerOptions { MaxConcurrentBuilds = 2, GlobalStatusFile = _statusFile },
            Defaults = new ProjectDefaults
            {
                Unity = new UnityOptions { EditorVersion = "6000.0.47f1" },
                Publishing = new PublishingOptions { StagingFolder = @"C:\ci\staging" },
            },
        };

        foreach (var name in new[] { "Crash", "Mines", "Rocket" })
        {
            _options.Projects.Add(new ProjectOptions
            {
                Name = name,
                Repository = new RepositoryOptions
                {
                    Url = "https://example.invalid/" + name,
                    Branch = "HML",
                    WorkspacePath = $@"C:\ci\workspace\{name}",
                },
                Publishing = new PublishingOptions { ArtifactFolder = $@"C:\ci\artifacts\{name}" },
            });
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* limpeza best effort */ }
    }

    private GlobalStatusWriter Create() => new(
        _store,
        new StubSnapshotScheduler(),
        new StaticOptionsMonitor<CiOptions>(_options),
        new FakeClock(),
        NullLogger<GlobalStatusWriter>.Instance);

    private async Task<long> SeedFinishedAsync(string project, BuildStatus status)
    {
        var id = await _store.CreateAsync(new BuildRecord
        {
            CommitSha = new string('a', 40),
            CommitAuthor = "Fulano de Tal",
            CommitMessage = "mensagem",
            Project = project,
            Branch = "HML",
            Status = status,
            QueuedAt = DateTimeOffset.UnixEpoch,
        }, default);

        var record = await _store.GetAsync(id, default);
        await _store.UpdateAsync(record! with
        {
            Status = status,
            StartedAt = DateTimeOffset.UnixEpoch,
            FinishedAt = DateTimeOffset.UnixEpoch.AddMinutes(12),
            DurationSeconds = 720,
            PublishedPath = $@"C:\ci\artifacts\{project}\{project}-HML-20260914-aaaaaaa.zip",
            ArtifactSizeBytes = 1024 * 1024 * 70,
        }, default);

        return id;
    }

    [Fact]
    public async Task Escreve_uma_linha_por_projeto_configurado()
    {
        await SeedFinishedAsync("Crash", BuildStatus.Succeeded);
        await SeedFinishedAsync("Rocket", BuildStatus.Failed);

        await Create().WriteAsync(default);

        var text = await File.ReadAllTextAsync(_statusFile);

        Assert.Contains("CI LOCAL", text);
        Assert.Contains("Crash", text);
        Assert.Contains("Mines", text);
        Assert.Contains("Rocket", text);
        Assert.Contains("nunca", text); // Mines ainda nao construiu
    }

    [Fact]
    public async Task Build_em_execucao_aparece_como_construindo()
    {
        await _store.CreateAsync(new BuildRecord
        {
            CommitSha = new string('b', 40),
            Project = "Mines",
            Branch = "HML",
            Status = BuildStatus.Running,
            QueuedAt = DateTimeOffset.UnixEpoch,
            StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(3),
        }, default);

        await Create().WriteAsync(default);

        var line = Assert.Single(
            await File.ReadAllLinesAsync(_statusFile),
            l => l.StartsWith("Mines"));

        Assert.Contains("construindo", line);
    }

    [Fact]
    public async Task Duas_builds_terminando_juntas_nao_corrompem_o_arquivo()
    {
        await SeedFinishedAsync("Crash", BuildStatus.Succeeded);
        await SeedFinishedAsync("Mines", BuildStatus.Succeeded);
        await SeedFinishedAsync("Rocket", BuildStatus.Failed);

        var writer = Create();

        // Muito mais que duas, de proposito: sem o lock, o arquivo sai truncado
        // ou com duas versoes intercaladas, e com ele toda leitura e valida.
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => writer.WriteAsync(default)));

        var lines = await File.ReadAllLinesAsync(_statusFile);

        Assert.StartsWith("CI LOCAL", lines[0]);
        Assert.Single(lines, l => l.StartsWith("PROJETO"));
        Assert.Single(lines, l => l.StartsWith("Crash"));
        Assert.Single(lines, l => l.StartsWith("Mines"));
        Assert.Single(lines, l => l.StartsWith("Rocket"));
        Assert.Single(lines, l => l.StartsWith("Fila:"));
    }

    [Fact]
    public async Task Escrita_atrasada_nao_sobrescreve_o_estado_mais_novo()
    {
        // O cenario que o lock existe para cobrir: Crash termina, e enquanto o
        // escritor A ainda esta lendo o estado, Mines termina e o escritor B
        // reescreve o arquivo. Sem o lock, A terminaria depois e gravaria a sua
        // leitura antiga por cima, e o arquivo diria que Mines nunca construiu.
        await SeedFinishedAsync("Crash", BuildStatus.Succeeded);

        // O bloqueio fica depois da leitura do estado e antes da escrita, que e
        // exatamente a janela onde o retrato de A envelhece.
        var scheduler = new BlockingSnapshotScheduler();
        var writer = new GlobalStatusWriter(
            _store,
            scheduler,
            new StaticOptionsMonitor<CiOptions>(_options),
            new FakeClock(),
            NullLogger<GlobalStatusWriter>.Instance);

        scheduler.BlockNext();
        var slow = Task.Run(() => writer.WriteAsync(default));

        await scheduler.WaitUntilBlocked();
        await SeedFinishedAsync("Mines", BuildStatus.Succeeded);

        var fast = Task.Run(() => writer.WriteAsync(default));

        // Com o lock, B fica esperando A; sem ele, B termina aqui e A ainda vai
        // gravar o retrato velho por cima.
        await Task.WhenAny(fast, Task.Delay(300));

        scheduler.Release();
        await Task.WhenAll(slow, fast);

        var mines = Assert.Single(await File.ReadAllLinesAsync(_statusFile), l => l.StartsWith("Mines"));
        Assert.DoesNotContain("nunca", mines);
        Assert.Contains("ok", mines);
    }

    [Fact]
    public async Task Rodape_nao_contradiz_as_linhas_de_projeto()
    {
        // O arquivo e reescrito de dentro do pipeline, quando a build que acabou
        // ainda ocupa a vaga no scheduler. Se o rodape viesse do contador do
        // scheduler, diria "1 em execução" logo abaixo de uma linha FALHOU.
        await SeedFinishedAsync("Crash", BuildStatus.Failed);

        var writer = new GlobalStatusWriter(
            _store,
            new StubSnapshotScheduler { Result = new SchedulerSnapshot(0, 1, 2) },
            new StaticOptionsMonitor<CiOptions>(_options),
            new FakeClock(),
            NullLogger<GlobalStatusWriter>.Instance);

        await writer.WriteAsync(default);

        var text = await File.ReadAllTextAsync(_statusFile);

        Assert.Contains("Em execução: 0 de 2", text);
        Assert.DoesNotContain("construindo", text);
    }

    [Fact]
    public async Task Nao_deixa_arquivo_temporario_para_tras()
    {
        await SeedFinishedAsync("Crash", BuildStatus.Succeeded);

        var writer = Create();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => writer.WriteAsync(default)));

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Destino_indisponivel_nao_derruba_a_build()
    {
        // Um arquivo no lugar da pasta e o que se ve quando o compartilhamento cai.
        _options.Scheduler.GlobalStatusFile = Path.Combine(_root, "arquivo.txt", "_STATUS-GERAL.txt");
        await File.WriteAllTextAsync(Path.Combine(_root, "arquivo.txt"), "nao sou uma pasta");

        await Create().WriteAsync(default);
    }

    [Fact]
    public async Task Sem_caminho_configurado_nao_escreve_nada()
    {
        _options.Scheduler.GlobalStatusFile = null;

        await Create().WriteAsync(default);

        Assert.False(File.Exists(_statusFile));
    }
}

/// <summary>
/// Scheduler que segura a primeira chamada de Snapshot. Como o writer le todo o
/// estado antes de pedir o snapshot, e escreve logo depois, este e o ponto exato
/// em que o retrato de um escritor pode envelhecer enquanto outro grava.
/// </summary>
public sealed class BlockingSnapshotScheduler : IBuildScheduler
{
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new(false);
    private int _blockNext;

    public void BlockNext() => Interlocked.Exchange(ref _blockNext, 1);
    public Task WaitUntilBlocked() => _blocked.Task;
    public void Release() => _release.Set();

    public Task<long?> EnqueueAsync(
        ResolvedProject project, UnityLocalCI.Core.Git.CommitInfo commit, BuildTrigger trigger, CancellationToken ct)
        => Task.FromResult<long?>(1);

    public bool Cancel(long buildId) => false;

    public bool Paused { get; set; }

    public SchedulerSnapshot Snapshot()
    {
        if (Interlocked.Exchange(ref _blockNext, 0) == 1)
        {
            _blocked.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(10));
        }

        return new SchedulerSnapshot(0, 1, 2);
    }
}

public sealed class StubSnapshotScheduler : IBuildScheduler
{
    public SchedulerSnapshot Result { get; set; } = new(0, 1, 2);

    public Task<long?> EnqueueAsync(
        ResolvedProject project, UnityLocalCI.Core.Git.CommitInfo commit, BuildTrigger trigger, CancellationToken ct)
        => Task.FromResult<long?>(1);

    public bool Cancel(long buildId) => false;

    public bool Paused { get; set; }

    public SchedulerSnapshot Snapshot() => Result;
}
