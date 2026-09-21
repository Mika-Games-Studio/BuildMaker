using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UnityLocalCI.Core.Secrets;

/// <summary>O que o usuario precisa ver para autorizar no navegador.</summary>
public sealed record DeviceLogin(
    string UserCode,
    string VerificationUri,
    string DeviceCode,
    TimeSpan Interval,
    DateTimeOffset ExpiresAt);

public sealed class GitHubSignInException(string message) : Exception(message);

public interface IGitHubSignIn
{
    Task<DeviceLogin> StartAsync(string clientId, CancellationToken ct);

    /// <summary>Espera a autorizacao no navegador e devolve o token.</summary>
    Task<string> CompleteAsync(string clientId, DeviceLogin login, CancellationToken ct);
}

/// <summary>
/// Entrada pelo navegador, no fluxo de dispositivo do GitHub — o mesmo que
/// aparece em "digite este codigo em github.com/login/device".
///
/// Existe para ninguem precisar criar um token a mao, copiar e colar. O
/// programa mostra um codigo, a pessoa autoriza na conta dela, e o token chega
/// aqui e vai direto para o cofre do Windows.
///
/// O fluxo de dispositivo nao usa client secret, e e justamente por isso que
/// ele serve a um aplicativo instalado: nao ha segredo do aplicativo para
/// vazar junto com o executavel.
/// </summary>
public sealed class GitHubDeviceFlow : IGitHubSignIn
{
    /// <summary>Leitura de codigo e metadados do repositorio. O CI nunca escreve.</summary>
    public const string Scope = "repo";

    private const string CodeEndpoint = "https://github.com/login/device/code";
    private const string TokenEndpoint = "https://github.com/login/oauth/access_token";

    private readonly HttpClient _http;

    public GitHubDeviceFlow(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("UnityLocalCI");
    }

    public async Task<DeviceLogin> StartAsync(string clientId, CancellationToken ct)
    {
        var resposta = await PostAsync(CodeEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scope,
        }, ct).ConfigureAwait(false);

        if (Texto(resposta, "error") is { } erro)
            throw new GitHubSignInException(Explicar(erro, Texto(resposta, "error_description")));

        var codigoDoDispositivo = Texto(resposta, "device_code")
            ?? throw new GitHubSignInException("O GitHub nao devolveu o codigo do dispositivo.");

        var codigoDoUsuario = Texto(resposta, "user_code") ?? "";
        var endereco = Texto(resposta, "verification_uri") ?? "https://github.com/login/device";

        var intervalo = TimeSpan.FromSeconds(Math.Max(5, Numero(resposta, "interval") ?? 5));
        var expira = DateTimeOffset.UtcNow.AddSeconds(Numero(resposta, "expires_in") ?? 900);

        return new DeviceLogin(codigoDoUsuario, endereco, codigoDoDispositivo, intervalo, expira);
    }

    public async Task<string> CompleteAsync(string clientId, DeviceLogin login, CancellationToken ct)
    {
        var intervalo = login.Interval;

        while (DateTimeOffset.UtcNow < login.ExpiresAt)
        {
            await Task.Delay(intervalo, ct).ConfigureAwait(false);

            var resposta = await PostAsync(TokenEndpoint, new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["device_code"] = login.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, ct).ConfigureAwait(false);

            if (Texto(resposta, "access_token") is { Length: > 0 } token) return token;

            switch (Texto(resposta, "error"))
            {
                // Ainda nao autorizou: seguir esperando e o comportamento certo.
                case "authorization_pending":
                    continue;

                // O GitHub pede para ir mais devagar. Ignorar isto leva a bloqueio.
                case "slow_down":
                    intervalo += TimeSpan.FromSeconds(5);
                    continue;

                case null:
                    throw new GitHubSignInException("Resposta inesperada do GitHub ao buscar o token.");

                case var erro:
                    throw new GitHubSignInException(Explicar(erro, Texto(resposta, "error_description")));
            }
        }

        throw new GitHubSignInException("O código expirou antes da autorização. Tente conectar de novo.");
    }

    private async Task<JsonElement> PostAsync(string url, Dictionary<string, string> campos, CancellationToken ct)
    {
        using var conteudo = new FormUrlEncodedContent(campos);
        using var resposta = await _http.PostAsync(url, conteudo, ct).ConfigureAwait(false);

        var corpo = await resposta.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            using var documento = JsonDocument.Parse(corpo);
            return documento.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new GitHubSignInException(
                $"O GitHub respondeu algo que nao e JSON ({(int)resposta.StatusCode}). " +
                "Confira a conexao e o proxy da rede.");
        }
    }

    private static string? Texto(JsonElement elemento, string nome)
        => elemento.ValueKind == JsonValueKind.Object
           && elemento.TryGetProperty(nome, out var valor)
           && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;

    private static int? Numero(JsonElement elemento, string nome)
        => elemento.ValueKind == JsonValueKind.Object
           && elemento.TryGetProperty(nome, out var valor)
           && valor.TryGetInt32(out var numero)
            ? numero
            : null;

    /// <summary>
    /// Traduz o codigo de erro do GitHub para o que a pessoa precisa fazer. O
    /// texto cru ("expired_token") nao diz a ninguem qual e o proximo passo.
    /// </summary>
    private static string Explicar(string erro, string? descricao) => erro switch
    {
        "authorization_pending" => "A autorização ainda não foi concluída no navegador.",
        "expired_token" => "O código expirou antes da autorização. Tente conectar de novo.",
        "access_denied" => "A autorização foi recusada no navegador.",
        "unsupported_grant_type" or "incorrect_client_credentials" or "incorrect_device_code" =>
            "O GitHub recusou o Client ID configurado. Confira o Client ID do OAuth App.",
        "device_flow_disabled" =>
            "O OAuth App está com o fluxo de dispositivo desligado. Ligue 'Enable Device Flow' nas " +
            "configurações do App no GitHub.",
        _ => descricao is { Length: > 0 } ? $"{erro}: {descricao}" : erro,
    };
}
