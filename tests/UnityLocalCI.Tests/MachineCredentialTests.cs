using UnityLocalCI.App;
using UnityLocalCI.Core.Configuration;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O acesso ao Git e da maquina, nao de cada jogo.
///
/// A credencial nasceu por projeto, e estava errado: quem conecta uma vez
/// conecta para todos. O campo do projeto continua existindo, mas so como
/// excecao — outra conta, outra organizacao.
/// </summary>
public class MachineCredentialTests
{
    private static ProjectOptions Projeto(string? credencialPropria = null) => new()
    {
        Name = "CrashUnity",
        Enabled = true,
        Repository = new RepositoryOptions
        {
            Url = "https://github.com/OPAGames/CrashUnity.git",
            Branch = "crash-aviaturbo-hml",
            WorkspacePath = @"C:\ci\workspace\CrashUnity",
            PatCredentialName = credencialPropria,
        },
        Unity = new UnityOptions { EditorVersion = "2022.3.62f3" },
    };

    private static ProjectDefaults Padroes(string? credencialDaMaquina) => new()
    {
        Repository = new RepositoryDefaults { PatCredentialName = credencialDaMaquina },

        // A pasta de destino e uma so, para todos os projetos, e vem daqui.
        Publishing = new PublishingOptions { ArtifactFolder = @"D:\builds" },
    };

    [Fact]
    public void Projeto_sem_credencial_usa_a_da_maquina()
    {
        var resolvido = ProjectResolver.Resolve(Projeto(), Padroes("UnityLocalCI_GitHub"));

        Assert.Equal("UnityLocalCI_GitHub", resolvido.Repository.PatCredentialName);
    }

    /// <summary>A excecao continua valendo: outra organizacao, outra conta.</summary>
    [Fact]
    public void Credencial_do_projeto_ganha_da_credencial_da_maquina()
    {
        var resolvido = ProjectResolver.Resolve(Projeto("Token_DoOutroCliente"), Padroes("UnityLocalCI_GitHub"));

        Assert.Equal("Token_DoOutroCliente", resolvido.Repository.PatCredentialName);
    }

    /// <summary>
    /// Campo apagado na tela vira string vazia, e nao nulo. Tratar os dois de
    /// formas diferentes faria "apagar para herdar" virar "ficar sem credencial".
    /// </summary>
    [Fact]
    public void Campo_apagado_herda_igual_a_campo_nulo()
    {
        Assert.Equal(
            "UnityLocalCI_GitHub",
            ProjectResolver.Resolve(Projeto("   "), Padroes("UnityLocalCI_GitHub")).Repository.PatCredentialName);
    }

    [Fact]
    public void Sem_credencial_em_lugar_nenhum_o_repositorio_e_anonimo()
    {
        Assert.Null(ProjectResolver.Resolve(Projeto(), Padroes(null)).Repository.PatCredentialName);
    }

    [Fact]
    public void Herdar_nao_estraga_url_branch_nem_workspace()
    {
        var resolvido = ProjectResolver.Resolve(Projeto(), Padroes("UnityLocalCI_GitHub"));

        Assert.Equal("https://github.com/OPAGames/CrashUnity.git", resolvido.Repository.Url);
        Assert.Equal("crash-aviaturbo-hml", resolvido.Repository.Branch);
        Assert.Equal(@"C:\ci\workspace\CrashUnity", resolvido.Repository.WorkspacePath);
    }

    /// <summary>
    /// A validacao passou a olhar a credencial resolvida. Olhando so a do
    /// projeto, uma conexao de maquina apontando para credencial inexistente
    /// passaria batido e a falha apareceria no primeiro clone.
    /// </summary>
    [Fact]
    public void Validacao_reprova_credencial_da_maquina_que_nao_existe()
    {
        var options = new CiOptions
        {
            Projects = [Projeto()],
            Defaults = Padroes("UnityLocalCI_QueNaoExiste"),
        };

        options.Defaults.Unity.ExecuteMethod = "Builder.PerformBuild";
        options.Defaults.Unity.TimeoutMinutes = 90;
        options.Defaults.Publishing.StagingFolder = @"C:\ci\staging";

        var resultado = new CiOptionsValidator(new FakeCredentialStore()).Validate(null, options);

        Assert.True(resultado.Failed);
        Assert.Contains(resultado.Failures!, f => f.Contains("UnityLocalCI_QueNaoExiste", StringComparison.Ordinal));
        Assert.Contains(resultado.Failures!, f => f.Contains("conexão da máquina", StringComparison.Ordinal));
    }

    [Fact]
    public void Validacao_aceita_quando_a_credencial_da_maquina_existe()
    {
        var cofre = new FakeCredentialStore();
        cofre.Set("UnityLocalCI_GitHub", "token");

        var options = new CiOptions
        {
            Projects = [Projeto()],
            Defaults = Padroes("UnityLocalCI_GitHub"),
        };

        options.Defaults.Unity.ExecuteMethod = "Builder.PerformBuild";
        options.Defaults.Unity.TimeoutMinutes = 90;
        options.Defaults.Publishing.StagingFolder = @"C:\ci\staging";

        Assert.False(new CiOptionsValidator(cofre).Validate(null, options).Failed);
    }

    /// <summary>
    /// A listagem de branches usa a mesma credencial resolvida. Sem isto ela
    /// tentaria sem token justamente nos projetos que herdam — que passaram a
    /// ser todos.
    /// </summary>
    [Fact]
    public void A_listagem_de_branches_usa_a_credencial_herdada()
    {
        var cofre = new FakeCredentialStore();
        cofre.Set("UnityLocalCI_GitHub", "token-da-maquina");

        var git = new CapturingGitClient();
        var catalogo = new BranchCatalog(cofre, git);

        catalogo.EnsureLoaded(Projeto(), Padroes("UnityLocalCI_GitHub"));

        Assert.True(git.Chamado.Wait(TimeSpan.FromSeconds(5)), "a listagem nem chegou a ser pedida");
        Assert.Equal("token-da-maquina", git.UltimoContexto!.PersonalAccessToken);
    }
}
