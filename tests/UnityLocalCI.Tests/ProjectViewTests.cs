using System.ComponentModel;
using System.Drawing.Design;
using UnityLocalCI.App;
using UnityLocalCI.Core.Configuration;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// A tela de configuracao de um projeto. O que se testa aqui e o que uma
/// captura de tela nao mostra: de onde vem a versao do editor e quais campos
/// abrem caixa de selecao em vez de esperar o caminho digitado.
/// </summary>
public class ProjectViewTests : IDisposable
{
    private readonly string _raiz = Path.Combine(
        Path.GetTempPath(), "unitylocalci-view-" + Guid.NewGuid().ToString("N"));

    private string CriarProjetoUnity(string versao)
    {
        var settings = Path.Combine(_raiz, "ProjectSettings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "ProjectVersion.txt"), "m_EditorVersion: " + versao);
        return _raiz;
    }

    [Fact]
    public void Escolher_o_workspace_preenche_a_versao_do_editor()
    {
        var projeto = new ProjectOptions { Name = "Jogo" };
        var view = new ProjectView(projeto);

        Assert.Null(view.EditorVersion);

        view.WorkspacePath = CriarProjetoUnity("2022.3.62f3");

        Assert.Equal("2022.3.62f3", view.EditorVersion);
        Assert.Equal("2022.3.62f3", projeto.Unity?.EditorVersion);
    }

    /// <summary>
    /// O projeto no disco vale mais que o que estava gravado: buildar na versao
    /// errada produz um artefato que parece certo e nao e.
    /// </summary>
    [Fact]
    public void Versao_do_disco_corrige_a_que_estava_na_configuracao()
    {
        var projeto = new ProjectOptions
        {
            Name = "Jogo",
            Unity = new UnityOptions { EditorVersion = "6000.0.47f1" },
        };

        new ProjectView(projeto).WorkspacePath = CriarProjetoUnity("2021.3.45f1");

        Assert.Equal("2021.3.45f1", projeto.Unity!.EditorVersion);
    }

    [Fact]
    public void Abrir_um_projeto_ja_clonado_preenche_a_versao_que_faltava()
    {
        var projeto = new ProjectOptions
        {
            Name = "Jogo",
            Repository = new RepositoryOptions { WorkspacePath = CriarProjetoUnity("2022.3.62f3") },
        };

        _ = new ProjectView(projeto);

        Assert.Equal("2022.3.62f3", projeto.Unity?.EditorVersion);
    }

    [Fact]
    public void Workspace_inexistente_nao_inventa_versao()
    {
        var projeto = new ProjectOptions { Name = "Jogo" };
        var view = new ProjectView(projeto) { WorkspacePath = Path.Combine(_raiz, "ainda-nao-clonado") };

        Assert.Null(view.EditorVersion);
        Assert.Contains("workspace", view.DetectedEditorVersion, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(nameof(ProjectView.WorkspacePath), typeof(FolderPathEditor))]
    [InlineData(nameof(ProjectView.ArtifactFolder), typeof(FolderPathEditor))]
    [InlineData(nameof(ProjectView.ManualTriggerFile), typeof(FilePathEditor))]
    public void Campos_de_caminho_abrem_caixa_de_selecao(string propriedade, Type editorEsperado)
    {
        var descritor = TypeDescriptor.GetProperties(typeof(ProjectView))[propriedade];

        Assert.NotNull(descritor);
        Assert.IsType(editorEsperado, descritor!.GetEditor(typeof(UITypeEditor)));
    }

    [Theory]
    [InlineData(nameof(GeneralView.LogFolder), typeof(FolderPathEditor))]
    [InlineData(nameof(GeneralView.StagingFolder), typeof(FolderPathEditor))]
    [InlineData(nameof(GeneralView.DatabasePath), typeof(FilePathEditor))]
    [InlineData(nameof(GeneralView.GlobalStatusFile), typeof(FilePathEditor))]
    public void Caminhos_da_aba_geral_tambem(string propriedade, Type editorEsperado)
    {
        var descritor = TypeDescriptor.GetProperties(typeof(GeneralView))[propriedade];

        Assert.NotNull(descritor);
        Assert.IsType(editorEsperado, descritor!.GetEditor(typeof(UITypeEditor)));
    }

    /// <summary>
    /// A aba Geral escreve direto nos objetos de configuracao; se ela guardasse
    /// copias, Salvar gravaria os valores antigos sem ninguem perceber.
    /// </summary>
    [Fact]
    public void Aba_geral_escreve_na_configuracao_de_verdade()
    {
        var options = new CiOptions();

        var view = new GeneralView(options)
        {
            MaxConcurrentBuilds = 4,
            LogFolder = @"D:\ci\logs",
            StagingFolder = @"D:\ci\staging",
            EditorVersion = "2022.3.62f3",
        };

        Assert.Equal(4, options.Scheduler.MaxConcurrentBuilds);
        Assert.Equal(@"D:\ci\logs", options.State.LogFolder);
        Assert.Equal(@"D:\ci\staging", options.Defaults.Publishing.StagingFolder);
        Assert.Equal("2022.3.62f3", options.Defaults.Unity.EditorVersion);
        Assert.Equal(4, view.MaxConcurrentBuilds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
        GC.SuppressFinalize(this);
    }
}
