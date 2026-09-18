using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.App;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// A lista de branches que o campo Branch oferece.
///
/// Existe para tirar da frente um erro que so aparece na primeira build, e
/// disfarcado: com o nome da branch errado, o git diz que a referencia nao
/// existe, e quem le entende que o repositorio esta inacessivel.
/// </summary>
public class BranchListingTests : IDisposable
{
    private readonly string _raiz = Path.Combine(
        Path.GetTempPath(), "unitylocalci-branches-" + Guid.NewGuid().ToString("N"));

    private static GitContext Contexto() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ws"),
        RepositoryUrl = "https://github.com/OPAGames/CrashUnity.git",
        Branch = "",
        PersonalAccessToken = null,
    };

    [Fact]
    public async Task Le_as_branches_da_saida_do_ls_remote()
    {
        var runner = new RecordingProcessRunner
        {
            Respond = _ => new ProcessResult(0, false,
                "9f1c2b3d\trefs/heads/main\n" +
                "a1b2c3d4\trefs/heads/crash-aviaturbo-hml\n" +
                "b2c3d4e5\trefs/heads/feature/loja\n", ""),
        };

        var branches = await new GitClient(runner, NullLogger<GitClient>.Instance)
            .ListRemoteBranchesAsync(Contexto(), CancellationToken.None);

        Assert.Equal(["crash-aviaturbo-hml", "feature/loja", "main"], branches);
    }

    /// <summary>
    /// Perguntar ao servidor nao pode depender de existir clone: o projeto e
    /// cadastrado antes da primeira build.
    /// </summary>
    [Fact]
    public async Task Pergunta_pela_url_sem_precisar_de_workspace()
    {
        var runner = new RecordingProcessRunner();

        await new GitClient(runner, NullLogger<GitClient>.Instance)
            .ListRemoteBranchesAsync(Contexto(), CancellationToken.None);

        var pedido = runner.Requests.Single();

        Assert.Contains("ls-remote", pedido.Arguments);
        Assert.Contains("--heads", pedido.Arguments);
        Assert.Contains("https://github.com/OPAGames/CrashUnity.git", pedido.Arguments);
        Assert.NotNull(pedido.Timeout);
    }

    [Fact]
    public async Task O_pat_vai_por_cabecalho_tambem_na_listagem()
    {
        var runner = new RecordingProcessRunner();

        await new GitClient(runner, NullLogger<GitClient>.Instance)
            .ListRemoteBranchesAsync(Contexto() with { PersonalAccessToken = "segredo" }, CancellationToken.None);

        var pedido = runner.Requests.Single();

        Assert.Contains(pedido.Arguments, a => a.StartsWith("http.extraHeader=Authorization: Basic", StringComparison.Ordinal));
        Assert.NotNull(pedido.DisplayArguments);
        Assert.DoesNotContain(pedido.DisplayArguments!, a => a.Contains("segredo", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------- clone local

    [Fact]
    public void Le_as_branches_que_o_clone_local_ja_conhece()
    {
        var git = Path.Combine(_raiz, ".git");
        var remotos = Path.Combine(git, "refs", "remotes", "origin");
        Directory.CreateDirectory(Path.Combine(remotos, "feature"));

        File.WriteAllText(Path.Combine(remotos, "main"), new string('a', 40));
        File.WriteAllText(Path.Combine(remotos, "crash-aviaturbo-hml"), new string('b', 40));
        File.WriteAllText(Path.Combine(remotos, "feature", "loja"), new string('c', 40));

        // origin/HEAD e apelido da branch padrao, nao uma branch.
        File.WriteAllText(Path.Combine(remotos, "HEAD"), "ref: refs/remotes/origin/main");

        var branches = LocalRepositoryInfo.ReadKnownBranches(_raiz);

        Assert.Equal(["crash-aviaturbo-hml", "feature/loja", "main"], branches);
    }

    /// <summary>O git compacta refs em packed-refs sem avisar; ler so os soltos perderia branches.</summary>
    [Fact]
    public void Le_tambem_o_packed_refs()
    {
        var git = Path.Combine(_raiz, ".git");
        Directory.CreateDirectory(git);

        File.WriteAllText(Path.Combine(git, "packed-refs"), """
            # pack-refs with: peeled fully-peeled sorted
            9f1c2b3d4e5f60718293a4b5c6d7e8f901234567 refs/remotes/origin/main
            a1b2c3d4e5f60718293a4b5c6d7e8f9012345678 refs/remotes/origin/mines-fortune-stg
            b2c3d4e5f60718293a4b5c6d7e8f90123456789a refs/heads/local-que-nao-interessa
            """);

        Assert.Equal(["main", "mines-fortune-stg"], LocalRepositoryInfo.ReadKnownBranches(_raiz));
    }

    [Fact]
    public void Pasta_sem_repositorio_nao_tem_branch_nenhuma()
    {
        Directory.CreateDirectory(_raiz);

        Assert.Empty(LocalRepositoryInfo.ReadKnownBranches(_raiz));
        Assert.Empty(LocalRepositoryInfo.ReadKnownBranches(null));
    }

    // ----------------------------------------------------------------- a tela

    [Fact]
    public void O_campo_Branch_oferece_lista()
    {
        var descritor = TypeDescriptor.GetProperties(typeof(ProjectView))[nameof(ProjectView.Branch)];

        Assert.NotNull(descritor);
        Assert.IsType<BranchConverter>(descritor!.Converter);
        Assert.True(descritor.Converter.GetStandardValuesSupported(null));
    }

    /// <summary>
    /// Nao exclusiva: cadastrar o projeto antes de a branch de homologacao
    /// existir e normal, e a tela nao pode impedir isso.
    /// </summary>
    [Fact]
    public void A_lista_nao_impede_digitar_uma_branch_que_ainda_nao_existe()
    {
        Assert.False(new BranchConverter().GetStandardValuesExclusive(null));
    }

    /// <summary>
    /// O contexto que o PropertyGrid passa ao abrir o dropdown. Sem a instancia,
    /// o conversor nao tem de onde tirar a lista — foi assim que a lista apareceu
    /// vazia na tela.
    /// </summary>
    private sealed class ContextoDaGrade(object instancia) : ITypeDescriptorContext
    {
        public object? Instance => instancia;
        public IContainer? Container => null;
        public PropertyDescriptor? PropertyDescriptor => null;
        public object? GetService(Type serviceType) => null;
        public void OnComponentChanged() { }
        public bool OnComponentChanging() => true;
    }

    [Fact]
    public void O_dropdown_recebe_as_branches_do_repositorio_do_projeto()
    {
        const string url = "https://github.com/OPAGames/CrashUnity.git";

        var catalogo = new BranchCatalog(new FakeCredentialStore());
        catalogo.Seed(url, ["crash-aviaturbo-hml", "crash-aviaturbo-dev", "main"]);

        var projeto = new ProjectOptions
        {
            Name = "CrashUnity",
            Repository = new RepositoryOptions { Url = url, Branch = "main" },
        };

        var view = new ProjectView(projeto, catalogo);
        var descritor = TypeDescriptor.GetProperties(typeof(ProjectView))[nameof(ProjectView.Branch)]!;

        var valores = descritor.Converter.GetStandardValues(new ContextoDaGrade(view))!.Cast<string>().ToArray();

        Assert.Equal(["crash-aviaturbo-dev", "crash-aviaturbo-hml", "main"], valores);
    }

    /// <summary>
    /// Projeto de outro repositorio nao herda a lista do vizinho: cada URL tem a
    /// sua, e misturar faria alguem escolher uma branch que nao existe la.
    /// </summary>
    [Fact]
    public void Cada_repositorio_tem_a_propria_lista()
    {
        var catalogo = new BranchCatalog(new FakeCredentialStore());
        catalogo.Seed("https://github.com/OPAGames/CrashUnity.git", ["crash-aviaturbo-hml"]);

        var outro = new ProjectOptions
        {
            Name = "HumanXRobots",
            Repository = new RepositoryOptions { Url = "https://github.com/Mika-Games-Studio/HumanXRobots.git" },
        };

        Assert.Empty(new ProjectView(outro, catalogo).KnownBranches);
    }

    [Fact]
    public void Sem_catalogo_a_lista_fica_vazia_em_vez_de_quebrar()
    {
        var view = new ProjectView(new ProjectOptions { Name = "Jogo" });

        Assert.Empty(view.KnownBranches);
        Assert.Empty(new BranchConverter().GetStandardValues(null)!);
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
        GC.SuppressFinalize(this);
    }
}
