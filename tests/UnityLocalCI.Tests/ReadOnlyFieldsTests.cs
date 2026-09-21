using System.ComponentModel;
using UnityLocalCI.App;
using UnityLocalCI.Core.Configuration;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O que a grade de configuracao deixa editar, e o que ela so mostra.
///
/// Nome e credencial passaram a ser apenas visiveis. Nao e enfeite: o nome
/// identifica a fila, o arquivo de configuracao, o gatilho e o historico, e a
/// credencial vem da conexao da maquina — editar qualquer um dos dois na mao
/// quebra coisas que nao dao sinal na hora.
/// </summary>
public class ReadOnlyFieldsTests
{
    private static PropertyDescriptor Propriedade(string nome)
        => TypeDescriptor.GetProperties(typeof(ProjectView))[nome]
           ?? throw new InvalidOperationException($"a propriedade '{nome}' sumiu da tela");

    [Theory]
    [InlineData(nameof(ProjectView.Name))]
    [InlineData(nameof(ProjectView.Credencial))]
    public void Campo_aparece_mas_nao_deixa_editar(string nome)
    {
        var propriedade = Propriedade(nome);

        Assert.True(propriedade.IsBrowsable, "o campo precisa continuar visível");
        Assert.True(propriedade.IsReadOnly, "o campo não pode ser editável");
    }

    [Theory]
    [InlineData(nameof(ProjectView.Url))]
    [InlineData(nameof(ProjectView.Branch))]
    [InlineData(nameof(ProjectView.WorkspacePath))]
    [InlineData(nameof(ProjectView.ArtifactFolder))]
    [InlineData(nameof(ProjectView.BuildTarget))]
    [InlineData(nameof(ProjectView.Enabled))]
    public void O_resto_continua_editavel(string nome)
    {
        Assert.False(Propriedade(nome).IsReadOnly);
    }

    /// <summary>
    /// O campo cru sai da grade, mas continua existindo: e por ele que o botao
    /// "Conectar ao GitHub" e o arquivo do projeto definem a excecao.
    /// </summary>
    [Fact]
    public void A_credencial_crua_sai_da_grade_sem_sumir_do_modelo()
    {
        // A grade lista o que e "browsable"; o descritor continua existindo.
        Assert.False(Propriedade(nameof(ProjectView.PatCredentialName)).IsBrowsable);

        var projeto = new ProjectOptions { Name = "Jogo" };
        _ = new ProjectView(projeto) { PatCredentialName = "  Token_Especial  " };

        Assert.Equal("Token_Especial", projeto.Repository!.PatCredentialName);
    }

    [Fact]
    public void A_credencial_mostrada_diz_de_onde_ela_vem()
    {
        var doProjeto = new ProjectOptions { Name = "A", Repository = new RepositoryOptions { PatCredentialName = "Token_DoCliente" } };
        var daMaquina = new ProjectOptions { Name = "B" };
        var semNada = new ProjectOptions { Name = "C" };

        var padroes = new ProjectDefaults { Repository = new RepositoryDefaults { PatCredentialName = "UnityLocalCI_GitHub" } };

        Assert.Contains("exceção", new ProjectView(doProjeto, null, padroes).Credencial, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Token_DoCliente", new ProjectView(doProjeto, null, padroes).Credencial, StringComparison.Ordinal);

        Assert.Contains("máquina", new ProjectView(daMaquina, null, padroes).Credencial, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnityLocalCI_GitHub", new ProjectView(daMaquina, null, padroes).Credencial, StringComparison.Ordinal);

        Assert.Contains("Conectar ao GitHub", new ProjectView(semNada, null, new ProjectDefaults()).Credencial, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ plataforma

    [Fact]
    public void BuildTarget_oferece_lista_comecando_por_WebGL()
    {
        var conversor = Propriedade(nameof(ProjectView.BuildTarget)).Converter;

        Assert.IsType<BuildTargetConverter>(conversor);
        Assert.True(conversor.GetStandardValuesSupported(null));
        Assert.Equal("WebGL", conversor.GetStandardValues(null)!.Cast<string>().First());
    }

    /// <summary>
    /// O Unity tem alguma dezena de alvos. Travar a escolha nos seis da lista
    /// impediria de configurar um alvo legitimo que nao esta nela.
    /// </summary>
    [Fact]
    public void A_lista_de_plataformas_nao_impede_outro_alvo()
    {
        Assert.False(new BuildTargetConverter().GetStandardValuesExclusive(null));
    }

    [Fact]
    public void Projeto_vinculado_ja_nasce_em_WebGL()
    {
        var pasta = Path.Combine(Path.GetTempPath(), "unitylocalci-alvo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(pasta, "ProjectSettings"));
        File.WriteAllText(Path.Combine(pasta, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f3");

        try
        {
            Assert.Equal("WebGL", ProjectDraft.FromFolder(pasta, []).Project.Unity?.BuildTarget);
        }
        finally
        {
            Directory.Delete(pasta, recursive: true);
        }
    }
}
