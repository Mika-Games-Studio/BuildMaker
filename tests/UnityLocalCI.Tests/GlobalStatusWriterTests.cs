using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core;
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

    /// <summary>
    /// Um arquivo descartavel, dentro da pasta temporaria do teste.
    ///
    /// Em producao o caminho continua fixo em %LOCALAPPDATA%\BuildMaker, e nao
    /// ha configuracao que o mude — era justamente essa configuracao que, com um
    /// caminho relativo, fazia o programa escrever dentro da pasta de instalacao.
    /// O construtor aceita um caminho so para o teste.
    ///
    /// Ate aqui esta suite usava o arquivo de verdade: apagava o geral.json da
    /// maquina de quem rodasse os testes e tentava devolver o conteudo no
    /// Dispose. Funcionava quase sempre, e quando nao funcionava deixava o
    /// aplicativo com um status que descrevia um estado inventado — inclusive
    /// "1 build em execucao" para uma build que terminara dias antes.
    /// </summary>
    private readonly string _statusFile;

    private readonly InMemoryBuildStore _store = new();
    private readonly CiOptions _options;

    public GlobalStatusWriterTests()
    {
        Directory.CreateDirectory(_root);
        _statusFile = Path.Combine(_root, "geral.json");

        _options = new CiOptions
        {
            Scheduler = new SchedulerOptions { MaxConcurrentBuilds = 2 },
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
        try
        {
            // So a pasta temporaria: nada fora dela foi tocado.
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { /* limpeza best effort */ }

        GC.SuppressFinalize(this);
    }

    private GlobalStatusWriter Create() => new(
        _store,
        new StubSnapshotScheduler(),
        new StaticOptionsMonitor<CiOptions>(_options),
        new FakeClock(),
        NullLogger<GlobalStatusWriter>.Instance,
        _statusFile);

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

    /// <summary>O documento lido do arquivo, ja como JSON.</summary>
    private async Task<JsonElement> LerAsync()
        => JsonDocument.Parse(await File.ReadAllTextAsync(_statusFile)).RootElement;

    private async Task<JsonElement> ProjetoAsync(string nome)
    {
        var raiz = await LerAsync();
        return raiz.GetProperty("Projetos")
            .EnumerateArray()
            .Single(p => p.GetProperty("Projeto").GetString() == nome);
    }

    [Fact]
    public async Task Escreve_uma_entrada_por_projeto_configurado()
    {
        await SeedFinishedAsync("Crash", BuildStatus.Succeeded);
        await SeedFinishedAsync("Rocket", BuildStatus.Failed);

        await Create().WriteAsync(default);

        var projetos = (await LerAsync()).GetProperty("Projetos");

        Assert.Equal(3, projetos.GetArrayLength());
        Assert.Equal("SUCESSO", (await ProjetoAsync("Crash")).GetProperty("UltimaBuild").GetProperty("Status").GetString());
        Assert.Equal("FALHOU", (await ProjetoAsync("Rocket")).GetProperty("UltimaBuild").GetProperty("Status").GetString());

        // Mines ainda nao construiu: o campo existe como null, e nao some.
        Assert.Equal(JsonValueKind.Null, (await ProjetoAsync("Mines")).GetProperty("UltimaBuild").ValueKind);
    }

    [Fact]
    public async Task Build_em_execucao_aparece_como_em_execucao()
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

        var mines = await ProjetoAsync("Mines");

        Assert.True(mines.GetProperty("EmExecucao").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, mines.GetProperty("IniciouEm").ValueKind);
    }

    [Fact]
    public async Task Duas_builds_terminando_juntas_nao_corrompem_o_arquivo()
    {
        await SeedFinishedAsync("Crash", BuildStatus.Succeeded);
        await SeedFinishedAsync("Mines", BuildStatus.Succeeded);
        await SeedFinishedAsync("Rocket", BuildStatus.Failed);

        var writer = Create();

        // Muito mais que duas, de proposito: sem o lock, o arquivo sai truncado
        // ou com duas versoes intercaladas. Em JSON isso e ainda mais direto de
        // verificar — um arquivo meio escrito simplesmente nao parseia.
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => writer.WriteAsync(default)));

        var projetos = (await LerAsync()).GetProperty("Projetos");

        Assert.Equal(3, projetos.GetArrayLength());
        foreach (var nome in new[] { "Crash", "Mines", "Rocket" })
            Assert.Single(projetos.EnumerateArray(), p => p.GetProperty("Projeto").GetString() == nome);
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
            NullLogger<GlobalStatusWriter>.Instance,
            _statusFile);

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

        var mines = await ProjetoAsync("Mines");
        Assert.Equal("SUCESSO", mines.GetProperty("UltimaBuild").GetProperty("Status").GetString());
    }

    [Fact]
    public async Task O_contador_nao_contradiz_os_projetos()
    {
        // O arquivo e reescrito de dentro do pipeline, quando a build que acabou
        // ainda ocupa a vaga no scheduler. Se o contador viesse do scheduler,
        // diria "1 em execucao" num arquivo onde nenhum projeto esta rodando.
        await SeedFinishedAsync("Crash", BuildStatus.Failed);

        var writer = new GlobalStatusWriter(
            _store,
            new StubSnapshotScheduler { Result = new SchedulerSnapshot(0, 1, 2) },
            new StaticOptionsMonitor<CiOptions>(_options),
            new FakeClock(),
            NullLogger<GlobalStatusWriter>.Instance,
            _statusFile);

        await writer.WriteAsync(default);

        var raiz = await LerAsync();

        Assert.Equal(0, raiz.GetProperty("EmExecucao").GetInt32());
        Assert.Equal(2, raiz.GetProperty("MaximoSimultaneo").GetInt32());
        Assert.DoesNotContain(
            raiz.GetProperty("Projetos").EnumerateArray(),
            p => p.GetProperty("EmExecucao").GetBoolean());
    }

    [Fact]
    public async Task Nao_deixa_arquivo_temporario_para_tras()
    {
        await SeedFinishedAsync("Crash", BuildStatus.Succeeded);

        var writer = Create();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => writer.WriteAsync(default)));

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// Em producao o arquivo vai sempre para a pasta do aplicativo. Nao ha
    /// caminho a configurar: era essa configuracao que, com um caminho relativo,
    /// fazia o programa escrever dentro da propria pasta de instalacao.
    ///
    /// O teste afirma isso sobre o caminho padrao, sem escrever nele. Escrever
    /// no arquivo de verdade para provar que ele e o arquivo de verdade e o que
    /// esta suite fazia antes, e o preco era mexer nos dados de quem rodasse os
    /// testes.
    /// </summary>
    [Fact]
    public void O_caminho_padrao_fica_na_pasta_de_dados_do_aplicativo()
    {
        Assert.StartsWith(AppPaths.StatusFolder, AppPaths.DefaultGlobalStatusFile, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppPaths.DataRoot, AppPaths.StatusFolder, StringComparison.OrdinalIgnoreCase);

        // Fora da pasta do programa: desinstalar apaga o programa e nao o estado.
        Assert.DoesNotContain("Programs", AppPaths.DataRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Escreve_no_caminho_que_recebeu()
    {
        await SeedFinishedAsync("Crash", BuildStatus.Succeeded);

        await Create().WriteAsync(default);

        Assert.True(File.Exists(_statusFile));
        Assert.StartsWith(_root, _statusFile, StringComparison.OrdinalIgnoreCase);
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

    public Task<bool> CancelAsync(long buildId, CancellationToken ct) => Task.FromResult(false);

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

    public Task<bool> CancelAsync(long buildId, CancellationToken ct) => Task.FromResult(false);

    public bool Paused { get; set; }

    public SchedulerSnapshot Snapshot() => Result;
}
