using System.Net;
using System.Net.Http;
using UnityLocalCI.Core.Secrets;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// De quem e o token guardado.
///
/// Serve a confirmacao visual na tela: "conectado" nao diz se a conta e a do
/// time ou uma pessoal esquecida na maquina. E, de quebra, e aqui que se
/// descobre que um token guardado deixou de valer — antes de uma build falhar
/// por causa disso.
/// </summary>
public class GitHubProfileTests
{
    private sealed class Servidor(HttpStatusCode codigo, string corpo, byte[]? bytes = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Pedidos { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Pedidos.Add(request);

            var conteudo = request.RequestUri!.Host == "api.github.com"
                ? new StringContent(corpo, System.Text.Encoding.UTF8, "application/json")
                : (HttpContent)new ByteArrayContent(bytes ?? []);

            return Task.FromResult(new HttpResponseMessage(codigo) { Content = conteudo });
        }
    }

    private const string ContaOk = """
        {"login":"Mikael-Cavalcanti","name":"Mikael Cavalcanti","avatar_url":"https://avatars.githubusercontent.com/u/1?v=4"}
        """;

    [Fact]
    public async Task Le_login_nome_e_foto()
    {
        var conta = await new GitHubProfile(new HttpClient(new Servidor(HttpStatusCode.OK, ContaOk)))
            .ReadAsync("token-qualquer", CancellationToken.None);

        Assert.Equal("Mikael-Cavalcanti", conta!.Login);
        Assert.Equal("Mikael Cavalcanti", conta.Name);
        Assert.Equal("https://avatars.githubusercontent.com/u/1?v=4", conta.AvatarUrl);
    }

    /// <summary>Sem nome no perfil, o @ e o que aparece em destaque.</summary>
    [Fact]
    public void Sem_nome_preenchido_mostra_o_arroba()
    {
        Assert.Equal("fulano", new GitHubAccount("fulano", null, null).Display);
        Assert.Equal("fulano", new GitHubAccount("fulano", "   ", null).Display);
        Assert.Equal("Fulano da Silva", new GitHubAccount("fulano", " Fulano da Silva ", null).Display);
    }

    /// <summary>
    /// Token revogado devolve 401. Isso e informacao, nao erro: a tela precisa
    /// poder dizer "o que esta guardado nao vale mais".
    /// </summary>
    [Fact]
    public async Task Token_que_nao_vale_mais_devolve_nulo()
    {
        var conta = await new GitHubProfile(new HttpClient(new Servidor(HttpStatusCode.Unauthorized, "{}")))
            .ReadAsync("token-velho", CancellationToken.None);

        Assert.Null(conta);
    }

    [Fact]
    public async Task Resposta_sem_login_nao_vira_conta()
    {
        var conta = await new GitHubProfile(new HttpClient(new Servidor(HttpStatusCode.OK, """{"id":1}""")))
            .ReadAsync("token", CancellationToken.None);

        Assert.Null(conta);
    }

    [Fact]
    public async Task Corpo_que_nao_e_json_nao_derruba_a_tela()
    {
        var conta = await new GitHubProfile(new HttpClient(new Servidor(HttpStatusCode.OK, "<html>oi</html>")))
            .ReadAsync("token", CancellationToken.None);

        Assert.Null(conta);
    }

    [Fact]
    public async Task Sem_token_nem_chega_a_perguntar()
    {
        var servidor = new Servidor(HttpStatusCode.OK, ContaOk);

        Assert.Null(await new GitHubProfile(new HttpClient(servidor)).ReadAsync("   ", CancellationToken.None));
        Assert.Empty(servidor.Pedidos);
    }

    /// <summary>
    /// O token vai como Bearer, e o User-Agent tem que existir: a API do GitHub
    /// recusa pedido sem ele, e a falha apareceria como "nao conectado".
    /// </summary>
    [Fact]
    public async Task O_pedido_leva_bearer_e_user_agent()
    {
        var servidor = new Servidor(HttpStatusCode.OK, ContaOk);

        await new GitHubProfile(new HttpClient(servidor)).ReadAsync("  token-123  ", CancellationToken.None);

        var pedido = servidor.Pedidos[0];
        Assert.Equal("Bearer", pedido.Headers.Authorization!.Scheme);
        Assert.Equal("token-123", pedido.Headers.Authorization.Parameter);
        Assert.NotEmpty(pedido.Headers.UserAgent);
    }

    /// <summary>
    /// A foto e publica, e o token nao tem nada que ir junto: cada lugar a mais
    /// por onde ele passa e um lugar a mais de onde ele pode vazar.
    /// </summary>
    [Fact]
    public async Task A_foto_e_buscada_sem_o_token()
    {
        var servidor = new Servidor(HttpStatusCode.OK, ContaOk, [1, 2, 3, 4]);
        var perfil = new GitHubProfile(new HttpClient(servidor));

        var bytes = await perfil.ReadAvatarAsync("https://avatars.githubusercontent.com/u/1?v=4", CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], bytes);
        Assert.Null(servidor.Pedidos[0].Headers.Authorization);
    }

    [Fact]
    public async Task Foto_que_nao_veio_nao_vira_bytes()
    {
        var perfil = new GitHubProfile(new HttpClient(new Servidor(HttpStatusCode.NotFound, "{}")));

        Assert.Null(await perfil.ReadAvatarAsync("https://avatars.githubusercontent.com/u/1", CancellationToken.None));
        Assert.Null(await perfil.ReadAvatarAsync("", CancellationToken.None));
    }
}
