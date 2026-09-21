using UnityLocalCI.App;
using UnityLocalCI.Core.Secrets;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// Antes de mandar alguem para o navegador, olhar o que a maquina ja tem.
///
/// E o que faz a diferenca entre "registre um OAuth App, crie um token, cole o
/// nome da credencial" e um clique. Quem ja clonou por HTTPS aqui, ja usa o
/// GitHub Desktop ou ja rodou 'gh auth login' tem o acesso guardado — pedir
/// outro seria pedir o que ja esta na mao.
/// </summary>
public class GitHubDiscoveryTests
{
    private const string Nome = "UnityLocalCI_GitHub";

    [Fact]
    public void O_cofre_vem_antes_de_tudo()
    {
        var cofre = new FakeCredentialStore();
        cofre.Set(Nome, "ja-guardado");

        var conta = new GitHubDiscovery(cofre, () => "do-git", () => "do-gh").Procurar();

        Assert.NotNull(conta);
        Assert.Equal("ja-guardado", conta!.Token);
        Assert.True(conta.JaNoCofre);
    }

    [Fact]
    public void Sem_nada_no_cofre_usa_a_conta_do_git()
    {
        var conta = new GitHubDiscovery(new FakeCredentialStore(), () => "do-git", () => "do-gh").Procurar();

        Assert.Equal("do-git", conta!.Token);
        Assert.False(conta.JaNoCofre);
        Assert.Contains("Git", conta.Origem, StringComparison.Ordinal);
    }

    [Fact]
    public void Sem_conta_no_git_sobra_a_sessao_do_gh()
    {
        var conta = new GitHubDiscovery(new FakeCredentialStore(), () => null, () => "do-gh").Procurar();

        Assert.Equal("do-gh", conta!.Token);
        Assert.Contains("CLI", conta.Origem, StringComparison.Ordinal);
    }

    /// <summary>Maquina limpa: nada encontrado, e ai sim o navegador.</summary>
    [Fact]
    public void Maquina_sem_nada_guardado_nao_encontra_conta()
    {
        Assert.Null(new GitHubDiscovery(new FakeCredentialStore(), () => null, () => null).Procurar());
    }

    /// <summary>
    /// Resposta vazia e resposta em branco valem como ausencia. O 'git
    /// credential fill' devolve linha vazia quando o auxiliar nao tem nada, e
    /// tratar isso como token guardaria um segredo vazio no cofre.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resposta_vazia_do_git_nao_conta_como_conta(string resposta)
    {
        var conta = new GitHubDiscovery(new FakeCredentialStore(), () => resposta, () => "do-gh").Procurar();

        Assert.Equal("do-gh", conta!.Token);
    }

    /// <summary>
    /// Um caminho quebrado nao pode derrubar o outro: sem 'git' no PATH, o 'gh'
    /// ao lado ainda responde.
    /// </summary>
    [Fact]
    public void Falha_de_um_caminho_nao_impede_o_seguinte()
    {
        var conta = new GitHubDiscovery(
            new FakeCredentialStore(),
            () => throw new InvalidOperationException("git nao instalado"),
            () => "do-gh").Procurar();

        Assert.Equal("do-gh", conta!.Token);
    }

    [Fact]
    public void Guardar_grava_com_o_nome_fixo_da_maquina()
    {
        var cofre = new FakeCredentialStore();
        var busca = new GitHubDiscovery(cofre, () => "do-git", () => null);

        busca.Guardar(busca.Procurar()!);

        Assert.Equal("do-git", cofre.Read(Nome));
    }

    /// <summary>
    /// Quem ja estava no cofre fica como esta: reescrever o mesmo segredo so
    /// arriscaria perde-lo se a gravacao falhasse no meio.
    /// </summary>
    [Fact]
    public void Guardar_nao_reescreve_o_que_ja_estava_no_cofre()
    {
        var cofre = new ContandoEscritas();
        cofre.Set(Nome, "ja-guardado");

        var busca = new GitHubDiscovery(cofre, () => "do-git", () => null);
        busca.Guardar(busca.Procurar()!);

        Assert.Equal(0, cofre.Escritas);
    }

    /// <summary>Cofre indisponivel nao derruba a busca — ela segue para o git.</summary>
    [Fact]
    public void Cofre_indisponivel_cai_para_a_conta_do_git()
    {
        var conta = new GitHubDiscovery(new CofreQuebrado(), () => "do-git", () => null).Procurar();

        Assert.Equal("do-git", conta!.Token);
    }

    /// <summary>
    /// O id da configuracao ganha do embutido, para uma maquina apontar para
    /// outro OAuth App sem recompilar.
    /// </summary>
    [Fact]
    public void O_client_id_da_configuracao_ganha_do_embutido()
    {
        Assert.Equal("Iv1.abc", GitHubConnection.ClientIdEmVigor("  Iv1.abc  "));
    }

    /// <summary>Sem id em lugar nenhum, nulo — e a janela explica o que fazer.</summary>
    [Fact]
    public void Sem_client_id_em_lugar_nenhum_o_resultado_e_nulo()
    {
        if (GitHubConnection.BuiltInClientId.Length > 0) return;

        Assert.Null(GitHubConnection.ClientIdEmVigor("   "));
    }

    private sealed class ContandoEscritas : ICredentialStore
    {
        private readonly Dictionary<string, string> _valores = new(StringComparer.OrdinalIgnoreCase);

        public int Escritas { get; private set; }

        public void Set(string nome, string valor) => _valores[nome] = valor;
        public bool Exists(string credentialName) => _valores.ContainsKey(credentialName);
        public string? Read(string credentialName) => _valores.TryGetValue(credentialName, out var v) ? v : null;

        public void Write(string credentialName, string secret)
        {
            Escritas++;
            _valores[credentialName] = secret;
        }
    }

    private sealed class CofreQuebrado : ICredentialStore
    {
        public bool Exists(string credentialName) => throw new InvalidOperationException("cofre indisponível");
        public string? Read(string credentialName) => throw new InvalidOperationException("cofre indisponível");
        public void Write(string credentialName, string secret) => throw new InvalidOperationException("cofre indisponível");
    }
}
