using UnityLocalCI.App;
using UnityLocalCI.Core.Configuration;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// "Vincular projeto Unity": apontar uma pasta que ja existe e sair com o
/// cadastro preenchido. Sem a caixa de dialogo, que e a unica parte da tela que
/// nao da para testar.
/// </summary>
public class ProjectDraftTests : IDisposable
{
    private readonly string _raiz = Path.Combine(
        Path.GetTempPath(), "unitylocalci-draft-" + Guid.NewGuid().ToString("N"));

    private string CriarPasta(string nome, string? versaoUnity = null, string? remote = null, string? branch = null)
    {
        var pasta = Path.Combine(_raiz, nome);
        Directory.CreateDirectory(pasta);

        if (versaoUnity is not null)
        {
            var settings = Path.Combine(pasta, "ProjectSettings");
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "ProjectVersion.txt"), "m_EditorVersion: " + versaoUnity);
        }

        if (remote is not null)
        {
            var git = Path.Combine(pasta, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "config"), "[remote \"origin\"]\n\turl = " + remote + "\n");
            File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/" + (branch ?? "main") + "\n");
        }

        return pasta;
    }

    [Fact]
    public void Pasta_de_projeto_preenche_url_branch_e_versao()
    {
        var pasta = CriarPasta("CrashAviaTurbo", "2022.3.62f3", "https://dev.azure.com/empresa/Jogos/_git/Crash", "crash-aviaturbo-hml");

        var resultado = ProjectDraft.FromFolder(pasta, []);

        Assert.True(resultado.Recognized);
        Assert.Equal("CrashAviaTurbo", resultado.Project.Name);
        Assert.Equal("https://dev.azure.com/empresa/Jogos/_git/Crash", resultado.Project.Repository.Url);
        Assert.Equal("crash-aviaturbo-hml", resultado.Project.Repository.Branch);
        Assert.Equal("2022.3.62f3", resultado.Project.Unity?.EditorVersion);
    }

    /// <summary>
    /// O CI apaga o que nao esta commitado antes de cada build. Se o workspace
    /// apontasse para a pasta escolhida, vincular um projeto destruiria o
    /// trabalho em andamento de quem vinculou.
    /// </summary>
    [Fact]
    public void Workspace_nunca_e_a_pasta_escolhida()
    {
        var pasta = CriarPasta("MeuJogo", "2022.3.62f3", "https://exemplo/_git/Jogo");

        var projeto = ProjectDraft.FromFolder(pasta, []).Project;

        Assert.NotEqual(
            Path.GetFullPath(pasta),
            Path.GetFullPath(projeto.Repository.WorkspacePath));

        Assert.Equal(Path.Combine(ProjectDraft.DefaultWorkspaceRoot, "MeuJogo"), projeto.Repository.WorkspacePath);
    }

    /// <summary>
    /// Um projeto que comeca ligado sem pasta de destino so produziria falha na
    /// primeira build.
    /// </summary>
    [Fact]
    public void Projeto_vinculado_entra_desligado()
    {
        var pasta = CriarPasta("Jogo", "2022.3.62f3");

        Assert.False(ProjectDraft.FromFolder(pasta, []).Project.Enabled);
    }

    [Fact]
    public void Nome_repetido_ganha_sufixo()
    {
        var pasta = CriarPasta("Jogo", "2022.3.62f3");

        var existentes = new List<ProjectOptions>
        {
            new() { Name = "Jogo" },
            new() { Name = "Jogo2" },
        };

        Assert.Equal("Jogo3", ProjectDraft.FromFolder(pasta, existentes).Project.Name);
    }

    [Fact]
    public void Segue_a_pasta_de_workspace_que_os_outros_projetos_usam()
    {
        var pasta = CriarPasta("Novo", "2022.3.62f3");

        var existentes = new List<ProjectOptions>
        {
            new()
            {
                Name = "Antigo",
                Repository = new RepositoryOptions { WorkspacePath = @"D:\ci\workspace\Antigo" },
                ManualTriggerFile = @"D:\ci\gatilhos\Antigo.txt",
            },
        };

        var projeto = ProjectDraft.FromFolder(pasta, existentes).Project;

        Assert.Equal(@"D:\ci\workspace\Novo", projeto.Repository.WorkspacePath);
        Assert.Equal(@"D:\ci\gatilhos\Novo.txt", projeto.ManualTriggerFile);
    }

    [Fact]
    public void Herda_o_nome_da_credencial_de_um_projeto_ja_cadastrado()
    {
        var pasta = CriarPasta("Novo", "2022.3.62f3");

        var existentes = new List<ProjectOptions>
        {
            new()
            {
                Name = "Antigo",
                Repository = new RepositoryOptions { PatCredentialName = "UnityLocalCI_AzureDevOpsPat" },
            },
        };

        Assert.Equal(
            "UnityLocalCI_AzureDevOpsPat",
            ProjectDraft.FromFolder(pasta, existentes).Project.Repository.PatCredentialName);
    }

    [Fact]
    public void Pasta_que_nao_e_projeto_nem_repositorio_nao_e_reconhecida()
    {
        var pasta = CriarPasta("PastaQualquer");

        Assert.False(ProjectDraft.FromFolder(pasta, []).Recognized);
    }

    /// <summary>Projeto Unity sem git ainda serve: falta so a URL.</summary>
    [Fact]
    public void Projeto_sem_repositorio_vem_com_a_versao_e_sem_url()
    {
        var pasta = CriarPasta("SemGit", "2021.3.45f1");

        var resultado = ProjectDraft.FromFolder(pasta, []);

        Assert.True(resultado.Recognized);
        Assert.Equal("2021.3.45f1", resultado.EditorVersion);
        Assert.Equal("", resultado.Project.Repository.Url);
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
        GC.SuppressFinalize(this);
    }
}
