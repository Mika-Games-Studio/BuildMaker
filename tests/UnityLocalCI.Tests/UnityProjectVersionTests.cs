using UnityLocalCI.Core.Unity;
using Xunit;

namespace UnityLocalCI.Tests;

public class UnityProjectVersionTests : IDisposable
{
    private readonly string _raiz = Path.Combine(
        Path.GetTempPath(), "unitylocalci-versao-" + Guid.NewGuid().ToString("N"));

    private string CriarProjeto(string conteudo)
    {
        var settings = Path.Combine(_raiz, "ProjectSettings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "ProjectVersion.txt"), conteudo);
        return _raiz;
    }

    [Fact]
    public void Le_a_versao_da_raiz_do_projeto()
    {
        var projeto = CriarProjeto("""
            m_EditorVersion: 2022.3.62f3
            m_EditorVersionWithRevision: 2022.3.62f3 (1a2b3c4d5e6f)
            """);

        Assert.Equal("2022.3.62f3", UnityProjectVersion.Read(projeto));
    }

    /// <summary>
    /// A revisao entre parenteses nao entra: o Unity CLI nao aceita esse formato,
    /// e uma versao com revisao colada seria pior que nao ter detectado nada.
    /// </summary>
    [Fact]
    public void Ignora_a_linha_com_revisao()
    {
        var projeto = CriarProjeto("m_EditorVersionWithRevision: 6000.0.47f1 (abcdef123456)\r\nm_EditorVersion: 6000.0.47f1\r\n");

        Assert.Equal("6000.0.47f1", UnityProjectVersion.Read(projeto));
    }

    [Fact]
    public void Aceita_a_pasta_ProjectSettings_e_o_proprio_arquivo()
    {
        var projeto = CriarProjeto("m_EditorVersion: 2021.3.45f1");

        var settings = Path.Combine(projeto, "ProjectSettings");
        var arquivo = Path.Combine(settings, "ProjectVersion.txt");

        Assert.Equal("2021.3.45f1", UnityProjectVersion.Read(settings));
        Assert.Equal("2021.3.45f1", UnityProjectVersion.Read(arquivo));
    }

    [Fact]
    public void Pasta_que_nao_e_projeto_Unity_devolve_nulo()
    {
        Directory.CreateDirectory(_raiz);

        Assert.Null(UnityProjectVersion.Read(_raiz));
        Assert.Null(UnityProjectVersion.Read(Path.Combine(_raiz, "nao-existe")));
        Assert.Null(UnityProjectVersion.Read(""));
        Assert.Null(UnityProjectVersion.Read(null));
    }

    /// <summary>
    /// Arquivo truncado por um clone interrompido nao pode derrubar a tela de
    /// configuracao: detectar a versao e conveniencia, nao requisito.
    /// </summary>
    [Fact]
    public void Arquivo_sem_a_chave_devolve_nulo_em_vez_de_lancar()
    {
        var projeto = CriarProjeto("m_EditorVersionWithRevision: \r\n");

        Assert.Null(UnityProjectVersion.Read(projeto));
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
        GC.SuppressFinalize(this);
    }
}
