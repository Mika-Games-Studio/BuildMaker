using System.Text.Json;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O status e o historico em JSON.
///
/// Eram arquivos de texto alinhados a coluna e os testes conferiam espacamento.
/// Agora o consumidor e um programa, entao o que se fixa e outra coisa: que os
/// campos estao la, com os nomes certos, e que o JSON e valido e legivel.
/// </summary>
public class StatusDocumentsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 14, 14, 32, 0, TimeSpan.Zero);

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
            ErrorSummary = error,
        };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_build_vira_um_documento_com_o_que_interessa()
    {
        var doc = StatusDocuments.Describe(Build());

        Assert.Equal(42, doc.Id);
        Assert.Equal("SUCESSO", doc.Status);
        Assert.Equal("a1b2c3d", doc.Commit);
        Assert.Equal("Fulano de Tal", doc.Autor);
        Assert.Equal("corrige colisao do player", doc.Mensagem);
        Assert.Equal(760, doc.DuracaoSegundos);
        Assert.Equal(82_208_358, doc.TamanhoBytes);
    }

    /// <summary>
    /// So o nome. O caminho completo e de uma maquina especifica e nao ajuda
    /// quem le o arquivo em outra.
    /// </summary>
    [Fact]
    public void O_arquivo_aparece_pelo_nome_e_nao_pelo_caminho()
        => Assert.Equal("Crash-HML-20260914-a1b2c3d.zip", StatusDocuments.Describe(Build()).Arquivo);

    [Fact]
    public void Build_sem_artefato_nao_inventa_um_nome()
        => Assert.Null(StatusDocuments.Describe(Build(published: null, size: null)).Arquivo);

    /// <summary>
    /// O resumo de erro vem do log com quebras de linha. Em JSON isso vira '\n'
    /// no meio de um valor, que ninguem le.
    /// </summary>
    [Fact]
    public void O_erro_chega_em_uma_linha_so()
    {
        var doc = StatusDocuments.Describe(
            Build(status: BuildStatus.Failed, error: "Assets/Player.cs(42,9): error CS1002\r\n; esperado"));

        Assert.Equal("Assets/Player.cs(42,9): error CS1002 ; esperado", doc.Erro);
        Assert.DoesNotContain('\n', doc.Erro!);
    }

    [Fact]
    public void Build_que_deu_certo_nao_tem_erro()
        => Assert.Null(StatusDocuments.Describe(Build()).Erro);

    [Fact]
    public void O_status_do_projeto_sai_como_json_valido()
    {
        var document = new ProjectStatusDocument(
            Projeto: "Crash",
            GeradoEm: Noon,
            Atual: StatusDocuments.Describe(Build()),
            Anterior: StatusDocuments.Describe(Build(id: 41, status: BuildStatus.Failed)),
            Avisos: ["Brotli sem fallback"],
            Historico: [StatusDocuments.Describe(Build(id: 41))]);

        var raiz = Parse(StatusDocuments.Serialize(document));

        Assert.Equal("Crash", raiz.GetProperty("Projeto").GetString());
        Assert.Equal(42, raiz.GetProperty("Atual").GetProperty("Id").GetInt64());
        Assert.Equal("FALHOU", raiz.GetProperty("Anterior").GetProperty("Status").GetString());
        Assert.Equal("Brotli sem fallback", raiz.GetProperty("Avisos")[0].GetString());
        Assert.Equal(1, raiz.GetProperty("Historico").GetArrayLength());
    }

    /// <summary>
    /// A primeira build de um projeto nao tem anterior, e o campo precisa existir
    /// como null em vez de sumir: quem le o arquivo nao deveria ter que descobrir
    /// a diferenca entre "nao houve" e "esqueci de escrever".
    /// </summary>
    [Fact]
    public void Sem_build_anterior_o_campo_existe_como_nulo()
    {
        var document = new ProjectStatusDocument(
            "Crash", Noon, StatusDocuments.Describe(Build()), null, [], []);

        var raiz = Parse(StatusDocuments.Serialize(document));

        Assert.True(raiz.TryGetProperty("Anterior", out var anterior));
        Assert.Equal(JsonValueKind.Null, anterior.ValueKind);
    }

    [Fact]
    public void O_consolidado_lista_todos_os_projetos_e_a_fila()
    {
        var document = new GlobalStatusDocument(
            GeradoEm: Noon,
            NaFila: 2,
            EmExecucao: 1,
            MaximoSimultaneo: 2,
            Projetos:
            [
                new GlobalProjectDocument("Crash", true, Noon.AddMinutes(-3), StatusDocuments.Describe(Build())),
                new GlobalProjectDocument("Outro", false, null, null),
            ]);

        var raiz = Parse(StatusDocuments.Serialize(document));

        Assert.Equal(2, raiz.GetProperty("NaFila").GetInt32());
        Assert.Equal(1, raiz.GetProperty("EmExecucao").GetInt32());
        Assert.Equal(2, raiz.GetProperty("Projetos").GetArrayLength());
        Assert.True(raiz.GetProperty("Projetos")[0].GetProperty("EmExecucao").GetBoolean());
        Assert.Equal(JsonValueKind.Null, raiz.GetProperty("Projetos")[1].GetProperty("UltimaBuild").ValueKind);
    }

    /// <summary>
    /// O arquivo e para ser aberto e lido. "EM EXECUÇÃO" escapado em \u00c7 e
    /// tecnicamente valido e praticamente inutil.
    /// </summary>
    [Fact]
    public void Os_acentos_ficam_legiveis_no_arquivo()
    {
        var json = StatusDocuments.Serialize(StatusDocuments.Describe(Build(status: BuildStatus.Running)));

        Assert.Contains("EM EXECUÇÃO", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-1, "—")]
    [InlineData(45, "45 s")]
    [InlineData(760, "12 min 40 s")]
    [InlineData(3600, "1 h 00 min")]
    public void A_duracao_e_dita_na_maior_unidade_que_cabe(int? seconds, string expected)
        => Assert.Equal(expected, StatusFormatter.FormatDuration(seconds));

    [Theory]
    [InlineData(BuildStatus.Succeeded, "SUCESSO")]
    [InlineData(BuildStatus.Failed, "FALHOU")]
    [InlineData(BuildStatus.Cancelled, "CANCELADA")]
    [InlineData(BuildStatus.Interrupted, "INTERROMPIDA")]
    [InlineData(BuildStatus.Running, "EM EXECUÇÃO")]
    [InlineData(BuildStatus.Queued, "NA FILA")]
    public void Cada_estado_tem_um_nome_so(BuildStatus status, string expected)
        => Assert.Equal(expected, StatusFormatter.Label(status));
}
