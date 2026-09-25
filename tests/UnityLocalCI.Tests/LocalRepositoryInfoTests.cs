using UnityLocalCI.Core.Git;
using Xunit;

namespace UnityLocalCI.Tests;

public class LocalRepositoryInfoTests : IDisposable
{
    private readonly string _raiz = Path.Combine(
        Path.GetTempPath(), "unitylocalci-repo-" + Guid.NewGuid().ToString("N"));

    private string CriarRepositorio(string config, string head = "ref: refs/heads/hml\n")
    {
        var git = Path.Combine(_raiz, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "config"), config);
        File.WriteAllText(Path.Combine(git, "HEAD"), head);
        return _raiz;
    }

    [Fact]
    public void Le_a_url_do_origin_e_a_branch_atual()
    {
        var repo = CriarRepositorio("""
            [core]
            	repositoryformatversion = 0
            [remote "origin"]
            	url = https://dev.azure.com/empresa/Jogos/_git/Crash
            	fetch = +refs/heads/*:refs/remotes/origin/*
            [branch "hml"]
            	remote = origin
            """);

        var info = LocalRepositoryInfo.Read(repo);

        Assert.NotNull(info);
        Assert.Equal("https://dev.azure.com/empresa/Jogos/_git/Crash", info!.Url);
        Assert.Equal("hml", info.Branch);
    }

    /// <summary>
    /// A pasta escolhida costuma ser a do projeto Unity, que nem sempre e a raiz
    /// do repositorio.
    /// </summary>
    [Fact]
    public void Encontra_o_repositorio_a_partir_de_uma_subpasta()
    {
        CriarRepositorio("[remote \"origin\"]\n\turl = git@servidor:time/jogo.git\n");

        var subpasta = Path.Combine(_raiz, "cliente", "UnityProject");
        Directory.CreateDirectory(subpasta);

        var info = LocalRepositoryInfo.Read(subpasta);

        Assert.Equal("git@servidor:time/jogo.git", info?.Url);
    }

    [Fact]
    public void Remote_renomeado_ainda_serve_para_preencher_a_tela()
    {
        var repo = CriarRepositorio("[remote \"azure\"]\n\turl = https://exemplo/_git/Jogo\n");

        Assert.Equal("https://exemplo/_git/Jogo", LocalRepositoryInfo.Read(repo)?.Url);
    }

    [Fact]
    public void HEAD_solto_nao_tem_branch_para_observar()
    {
        var repo = CriarRepositorio(
            "[remote \"origin\"]\n\turl = https://exemplo/_git/Jogo\n",
            head: "9f1c2b3d4e5f60718293a4b5c6d7e8f901234567\n");

        var info = LocalRepositoryInfo.Read(repo);

        Assert.NotNull(info);
        Assert.Null(info!.Branch);
        Assert.Equal("https://exemplo/_git/Jogo", info.Url);
    }

    [Fact]
    public void Pasta_sem_repositorio_devolve_nulo()
    {
        Directory.CreateDirectory(_raiz);

        Assert.Null(LocalRepositoryInfo.Read(_raiz));
        Assert.Null(LocalRepositoryInfo.Read(""));
        Assert.Null(LocalRepositoryInfo.Read(null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
        GC.SuppressFinalize(this);
    }
}
