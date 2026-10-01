using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Hosting;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

public class StartupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "unitylocalci-tests", Guid.NewGuid().ToString("N"));

    private readonly InMemoryBuildStore _store = new();

    public StartupServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { /* limpeza best effort */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Uma build que ficou em Running porque o processo morreu no meio dela.
    ///
    /// Acontece de verdade nesta maquina: a heuristica de ransomware do
    /// antivirus corporativo mata o programa durante o empacotamento, quando o
    /// pipeline escreve a saida WebGL inteira no staging de uma vez.
    /// </summary>
    private async Task<long> SemearBuildOrfaAsync(string projeto)
    {
        var id = await _store.CreateAsync(new BuildRecord
        {
            CommitSha = new string('b', 40),
            CommitAuthor = "Fulano de Tal",
            CommitMessage = "commit que ficou pela metade",
            Project = projeto,
            Branch = "main",
            Status = BuildStatus.Queued,
            QueuedAt = DateTimeOffset.UnixEpoch,
        }, default);

        var registro = await _store.GetAsync(id, default);
        await _store.UpdateAsync(registro! with
        {
            Status = BuildStatus.Running,
            StartedAt = DateTimeOffset.UnixEpoch,
        }, default);

        return id;
    }

    private static CiOptions Configuracao(string projeto, string raiz) => new()
    {
        Scheduler = new SchedulerOptions { MaxConcurrentBuilds = 2 },
        Defaults = new ProjectDefaults
        {
            Unity = new UnityOptions { EditorVersion = "6000.0.47f1" },
            Publishing = new PublishingOptions
            {
                StagingFolder = Path.Combine(raiz, "staging"),
                ArtifactFolder = Path.Combine(raiz, "artefatos"),
            },
        },
        Projects =
        {
            new ProjectOptions
            {
                Name = projeto,
                Repository = new RepositoryOptions
                {
                    Url = "https://example.invalid/" + projeto,
                    Branch = "main",
                    WorkspacePath = Path.Combine(raiz, "workspace", projeto),
                },
            },
        },
    };

    private async Task IniciarAsync(CiOptions opcoes, string arquivoDeStatus)
    {
        var escritor = new GlobalStatusWriter(
            _store,
            new StubSnapshotScheduler { Result = new SchedulerSnapshot(0, 0, 2) },
            new StaticOptionsMonitor<CiOptions>(opcoes),
            new FakeClock(),
            NullLogger<GlobalStatusWriter>.Instance,
            arquivoDeStatus);

        var servico = new StartupService(
            _store,
            new OrphanRecovery(_store, Options.Create(opcoes), new FakeClock(), NullLogger<OrphanRecovery>.Instance),
            escritor,
            Options.Create(opcoes),
            NullLogger<StartupService>.Instance);

        await servico.StartAsync(default);
    }

    [Fact]
    public async Task O_status_geral_deixa_de_anunciar_a_build_que_morreu_junto_com_o_processo()
    {
        // A recuperacao ja fechava o registro no banco, mas o geral.json so era
        // reescrito de dentro do pipeline. Quem abrisse o arquivo depois de uma
        // queda via "em execucao" para uma build que nao existia mais — ate a
        // proxima build terminar, o que podia demorar dias.
        var opcoes = Configuracao("HumanXRobots", _root);
        var arquivo = Path.Combine(_root, "geral.json");
        await SemearBuildOrfaAsync("HumanXRobots");

        await IniciarAsync(opcoes, arquivo);

        Assert.True(File.Exists(arquivo));

        var documento = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(arquivo)).RootElement;
        Assert.Equal(0, documento.GetProperty("EmExecucao").GetInt32());

        var projeto = documento.GetProperty("Projetos").EnumerateArray().Single();
        Assert.False(projeto.GetProperty("EmExecucao").GetBoolean());
    }

    [Fact]
    public async Task A_build_interrompida_aparece_no_status_com_o_motivo()
    {
        var opcoes = Configuracao("HumanXRobots", _root);
        var arquivo = Path.Combine(_root, "geral.json");
        await SemearBuildOrfaAsync("HumanXRobots");

        await IniciarAsync(opcoes, arquivo);

        var documento = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(arquivo)).RootElement;
        var ultima = documento.GetProperty("Projetos").EnumerateArray().Single().GetProperty("UltimaBuild");

        Assert.Equal("INTERROMPIDA", ultima.GetProperty("Status").GetString());
    }

    [Fact]
    public async Task O_registro_no_banco_e_fechado_como_interrompido()
    {
        var opcoes = Configuracao("HumanXRobots", _root);
        var id = await SemearBuildOrfaAsync("HumanXRobots");

        await IniciarAsync(opcoes, Path.Combine(_root, "geral.json"));

        var registro = await _store.GetAsync(id, default);
        Assert.Equal(BuildStatus.Interrupted, registro!.Status);
        Assert.NotNull(registro.FinishedAt);
        Assert.NotNull(registro.ErrorSummary);
    }

    [Fact]
    public async Task Sem_build_orfa_o_status_geral_e_escrito_do_mesmo_jeito()
    {
        // Subir o programa numa maquina limpa tem que produzir um arquivo de
        // status, e nao a ausencia dele: quem abre a pasta antes da primeira
        // build precisa ver o CI de pe, com a lista de projetos.
        var opcoes = Configuracao("HumanXRobots", _root);
        var arquivo = Path.Combine(_root, "geral.json");

        await IniciarAsync(opcoes, arquivo);

        Assert.True(File.Exists(arquivo));

        var documento = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(arquivo)).RootElement;
        Assert.Equal(0, documento.GetProperty("EmExecucao").GetInt32());
        Assert.Single(documento.GetProperty("Projetos").EnumerateArray());
    }
}
