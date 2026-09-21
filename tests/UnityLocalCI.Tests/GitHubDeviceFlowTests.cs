using System.Net;
using System.Net.Http;
using UnityLocalCI.Core.Secrets;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// Entrada pelo navegador, no fluxo de dispositivo do GitHub.
///
/// O que se testa aqui e o laco de espera: e nele que um engano vira ou um
/// programa que desiste cedo demais, ou um que martela o GitHub ate ser
/// bloqueado.
/// </summary>
public class GitHubDeviceFlowTests
{
    private sealed class RespostasFalsas(params string[] corpos) : HttpMessageHandler
    {
        private int _proxima;

        public List<string> Pedidos { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Pedidos.Add(await request.Content!.ReadAsStringAsync(ct));

            var corpo = corpos[Math.Min(_proxima++, corpos.Length - 1)];

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(corpo, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static GitHubDeviceFlow Criar(RespostasFalsas respostas) => new(new HttpClient(respostas));

    private const string CodigoOk = """
        {"device_code":"dc-123","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}
        """;

    [Fact]
    public async Task Comeca_devolvendo_o_codigo_para_o_usuario_digitar()
    {
        var respostas = new RespostasFalsas(CodigoOk);

        var login = await Criar(respostas).StartAsync("client-abc", CancellationToken.None);

        Assert.Equal("ABCD-1234", login.UserCode);
        Assert.Equal("https://github.com/login/device", login.VerificationUri);
        Assert.Contains("client_id=client-abc", respostas.Pedidos[0], StringComparison.Ordinal);
        Assert.Contains("scope=repo", respostas.Pedidos[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Enquanto ninguem autorizou, o GitHub responde 'authorization_pending'.
    /// Tratar isso como falha faria o programa desistir antes de a pessoa
    /// terminar de digitar o codigo.
    /// </summary>
    [Fact]
    public async Task Espera_a_autorizacao_e_devolve_o_token()
    {
        var respostas = new RespostasFalsas(
            """{"error":"authorization_pending"}""",
            """{"error":"authorization_pending"}""",
            """{"access_token":"gho_token","token_type":"bearer"}""");

        var login = new DeviceLogin("ABCD-1234", "https://github.com/login/device", "dc-123",
            TimeSpan.Zero, DateTimeOffset.UtcNow.AddMinutes(5));

        var token = await Criar(respostas).CompleteAsync("client-abc", login, CancellationToken.None);

        Assert.Equal("gho_token", token);
        Assert.Equal(3, respostas.Pedidos.Count);
        Assert.Contains("device_code=dc-123", respostas.Pedidos[0], StringComparison.Ordinal);
    }

    /// <summary>Ignorar 'slow_down' leva a bloqueio temporario pelo GitHub.</summary>
    [Fact]
    public async Task Slow_down_aumenta_o_intervalo_em_vez_de_martelar()
    {
        var respostas = new RespostasFalsas(
            """{"error":"slow_down","interval":10}""",
            """{"access_token":"gho_token"}""");

        var login = new DeviceLogin("A", "u", "dc", TimeSpan.Zero, DateTimeOffset.UtcNow.AddMinutes(5));

        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var token = await Criar(respostas).CompleteAsync("c", login, CancellationToken.None);
        relogio.Stop();

        Assert.Equal("gho_token", token);
        Assert.True(relogio.Elapsed >= TimeSpan.FromSeconds(5),
            $"deveria ter esperado mais depois do slow_down, mas levou {relogio.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public async Task Recusa_no_navegador_vira_mensagem_em_portugues()
    {
        var respostas = new RespostasFalsas("""{"error":"access_denied"}""");
        var login = new DeviceLogin("A", "u", "dc", TimeSpan.Zero, DateTimeOffset.UtcNow.AddMinutes(5));

        var erro = await Assert.ThrowsAsync<GitHubSignInException>(
            () => Criar(respostas).CompleteAsync("c", login, CancellationToken.None));

        Assert.Contains("recusada", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Client_id_errado_diz_que_o_problema_e_o_client_id()
    {
        var respostas = new RespostasFalsas("""{"error":"incorrect_client_credentials"}""");

        var erro = await Assert.ThrowsAsync<GitHubSignInException>(
            () => Criar(respostas).StartAsync("client-errado", CancellationToken.None));

        Assert.Contains("Client ID", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codigo_expirado_pede_para_tentar_de_novo()
    {
        var respostas = new RespostasFalsas("""{"error":"authorization_pending"}""");

        var login = new DeviceLogin("A", "u", "dc", TimeSpan.Zero, DateTimeOffset.UtcNow.AddMilliseconds(-1));

        var erro = await Assert.ThrowsAsync<GitHubSignInException>(
            () => Criar(respostas).CompleteAsync("c", login, CancellationToken.None));

        Assert.Contains("expirou", erro.Message, StringComparison.OrdinalIgnoreCase);
    }
}
