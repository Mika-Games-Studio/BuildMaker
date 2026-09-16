using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

public class StatusFormatterTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 14, 14, 32, 0, TimeSpan.Zero);

    /// <summary>
    /// Os arquivos sao escritos na hora local de quem le. Os testes derivam o
    /// esperado da mesma conversao para nao quebrarem em outro fuso.
    /// </summary>
    private static string Expected(DateTimeOffset instant, string format)
        => instant.ToLocalTime().ToString(format);

    private static BuildRecord Build(
        long id = 42,
        BuildStatus status = BuildStatus.Succeeded,
        string? error = null,
        string? published = @"\\build01\builds\crash\hml\Crash-HML-20260914-a1b2c3d.zip",
        long? size = 82_208_358,
        int duration = 760)
        => new()
        {
            Id = id,
            CommitSha = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0",
            CommitMessage = "corrige colisao do player",
            CommitAuthor = "Fulano de Tal",
            Project = "Crash",
            Branch = "HML",
            Status = status,
            QueuedAt = Noon.AddMinutes(-20),
            StartedAt = Noon.AddSeconds(-duration),
            FinishedAt = Noon,
            DurationSeconds = duration,
            PublishedPath = published,
            ArtifactSizeBytes = size,
            LogPath = @"C:\ci\logs\build-42.log",
            ErrorSummary = error,
        };

    // ------------------------------------------------------------ _STATUS.txt

    [Fact]
    public void Status_de_sucesso_traz_os_campos_da_especificacao()
    {
        var text = StatusFormatter.FormatProjectStatus(
            Build(), previous: null, warnings: Array.Empty<string>(), playHint: @"abra latest\rodar.bat");

        Assert.StartsWith("ÚLTIMA BUILD: SUCESSO", text);
        Assert.Contains("Commit....: a1b2c3d  \"corrige colisao do player\"", text);
        Assert.Contains("Autor.....: Fulano de Tal", text);
        Assert.Contains("Duração...: 12 min 40 s", text);
        Assert.Contains("Crash-HML-20260914-a1b2c3d.zip", text);
        Assert.Contains(@"Jogar.....: abra latest\rodar.bat", text);
    }

    [Fact]
    public void Status_de_sucesso_nao_mostra_bloco_de_erro()
    {
        var text = StatusFormatter.FormatProjectStatus(
            Build(), null, Array.Empty<string>(), null);

        Assert.DoesNotContain("Erro", text);
    }

    [Fact]
    public void Status_de_falha_traz_o_erro_resumido_e_o_caminho_do_log()
    {
        var build = Build(
            status: BuildStatus.Failed,
            error: @"Assets\Scripts\Player.cs(42,17): error CS0103: The name 'velocty' does not exist",
            published: null,
            size: null);

        var text = StatusFormatter.FormatProjectStatus(build, null, Array.Empty<string>(), null);

        Assert.StartsWith("ÚLTIMA BUILD: FALHOU", text);
        Assert.Contains("CS0103", text);
        Assert.Contains(@"ver _logs\build-42.log", text);
        // Sem zip publicado, nao pode haver linha de arquivo nem de "jogar".
        Assert.DoesNotContain("Arquivo...", text);
        Assert.DoesNotContain("Jogar", text);
    }

    [Fact]
    public void Erro_de_varias_linhas_fica_indentado_em_vez_de_virar_uma_linha_so()
    {
        var build = Build(
            status: BuildStatus.Failed,
            error: "primeiro erro\nsegundo erro\nterceiro erro",
            published: null);

        var text = StatusFormatter.FormatProjectStatus(build, null, Array.Empty<string>(), null);
        var lines = text.Split(Environment.NewLine);

        Assert.Contains(lines, l => l.StartsWith("Erro......: primeiro erro"));
        Assert.Contains(lines, l => l.StartsWith("            segundo erro"));
        Assert.Contains(lines, l => l.StartsWith("            terceiro erro"));
    }

    [Fact]
    public void Aviso_do_builder_aparece_destacado_no_status()
    {
        // A regra 6b exige que o alerta de compressao apareca no log E no
        // _STATUS.txt; sem isso o sintoma seria tela preta sem explicacao.
        var text = StatusFormatter.FormatProjectStatus(
            Build(),
            previous: null,
            warnings: new[] { "compressao Brotli com decompressionFallback desligado" },
            playHint: null);

        Assert.Contains("ATENÇÃO:", text);
        Assert.Contains("  - compressao Brotli com decompressionFallback desligado", text);
    }

    [Fact]
    public void Status_mostra_a_build_anterior()
    {
        var previous = Build(id: 41, status: BuildStatus.Failed);
        var text = StatusFormatter.FormatProjectStatus(Build(), previous, Array.Empty<string>(), null);

        Assert.Contains("Build anterior: " + Expected(Noon, "dd/MM/yyyy HH:mm"), text);
        Assert.Contains("FALHOU", text);
    }

    // --------------------------------------------------------- _HISTORICO.txt

    [Fact]
    public void Historico_tem_uma_linha_por_build()
    {
        var builds = new[] { Build(id: 40), Build(id: 41, status: BuildStatus.Failed), Build(id: 42) };

        var text = StatusFormatter.FormatHistory(builds);
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Contains("SUCESSO", lines[0]);
        Assert.Contains("FALHOU", lines[1]);
        Assert.Contains(@"ver _logs\build-41.log", lines[1]);
    }

    // ----------------------------------------------------- _STATUS-GERAL.txt

    private static string GlobalSample()
    {
        var rows = new[]
        {
            new GlobalStatusRow("Crash", false, null, Build()),
            new GlobalStatusRow("Mines", true, Noon.AddMinutes(9), null),
            new GlobalStatusRow("Rocket", false, null,
                Build(id: 39, status: BuildStatus.Failed, error: "erro", published: null)),
        };

        return StatusFormatter.FormatGlobalStatus(Noon.AddMinutes(15), rows, waiting: 0, running: 1, maxConcurrentBuilds: 2);
    }

    [Fact]
    public void Status_geral_tem_uma_linha_por_projeto()
    {
        var text = GlobalSample();

        Assert.Contains("CI LOCAL — " + Expected(Noon.AddMinutes(15), "dd/MM/yyyy HH:mm"), text);
        Assert.Contains("PROJETO", text);
        Assert.Contains("Crash", text);
        Assert.Contains("Mines", text);
        Assert.Contains("Rocket", text);
        Assert.Contains("Fila: 0 aguardando  |  Em execução: 1 de 2", text);
    }

    [Fact]
    public void Status_geral_distingue_ok_construindo_e_falhou()
    {
        var lines = GlobalSample().Split(Environment.NewLine);

        var crash = Assert.Single(lines, l => l.StartsWith("Crash"));
        var mines = Assert.Single(lines, l => l.StartsWith("Mines"));
        var rocket = Assert.Single(lines, l => l.StartsWith("Rocket"));

        Assert.Contains("ok", crash);
        Assert.Contains("Crash-HML-20260914-a1b2c3d.zip", crash);

        Assert.Contains("construindo", mines);
        Assert.Contains("(iniciou", mines);

        // O que precisa de atencao grita; o que esta bem, nao.
        Assert.Contains("FALHOU", rocket);
        Assert.Contains(@"ver _logs\build-39.log", rocket);
    }

    [Fact]
    public void Status_geral_alinha_as_colunas_mesmo_com_nomes_de_tamanhos_diferentes()
    {
        var rows = new[]
        {
            new GlobalStatusRow("A", false, null, Build()),
            new GlobalStatusRow("ProjetoComNomeBemLongo", false, null, Build()),
        };

        var lines = StatusFormatter
            .FormatGlobalStatus(Noon, rows, 0, 0, 2)
            .Split(Environment.NewLine);

        var header = Assert.Single(lines, l => l.StartsWith("PROJETO"));
        var curto = Assert.Single(lines, l => l.StartsWith("A "));
        var longo = Assert.Single(lines, l => l.StartsWith("ProjetoComNomeBemLongo"));

        // A coluna ESTADO comeca na mesma posicao nas tres linhas.
        Assert.Equal(header.IndexOf("ESTADO", StringComparison.Ordinal), curto.IndexOf("ok", StringComparison.Ordinal));
        Assert.Equal(header.IndexOf("ESTADO", StringComparison.Ordinal), longo.IndexOf("ok", StringComparison.Ordinal));
    }

    [Fact]
    public void Projeto_que_nunca_construiu_nao_quebra_o_status_geral()
    {
        var rows = new[] { new GlobalStatusRow("Novo", false, null, null) };

        var text = StatusFormatter.FormatGlobalStatus(Noon, rows, 0, 0, 2);

        Assert.Contains("Novo", text);
        Assert.Contains("nunca", text);
    }

    // ------------------------------------------------------------- duracoes

    [Theory]
    [InlineData(null, "—")]
    [InlineData(40, "40 s")]
    [InlineData(760, "12 min 40 s")]
    [InlineData(65, "1 min 05 s")]
    [InlineData(3900, "1 h 05 min")]
    public void Duracao_e_legivel(int? seconds, string expected)
        => Assert.Equal(expected, StatusFormatter.FormatDuration(seconds));
}
