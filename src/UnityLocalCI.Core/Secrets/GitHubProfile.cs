using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UnityLocalCI.Core.Secrets;

/// <summary>Quem esta conectado, do jeito que o GitHub conta.</summary>
/// <param name="Login">O @ da pessoa.</param>
/// <param name="Name">O nome, quando ela preencheu o perfil.</param>
/// <param name="AvatarUrl">Endereco da foto. Publico — nao leva token junto.</param>
public sealed record GitHubAccount(string Login, string? Name, string? AvatarUrl)
{
    /// <summary>O que mostrar em destaque: o nome se houver, senao o @.</summary>
    public string Display => string.IsNullOrWhiteSpace(Name) ? Login : Name!.Trim();
}

public interface IGitHubProfile
{
    /// <summary>Quem e o dono do token, ou nulo se ele nao vale mais.</summary>
    Task<GitHubAccount?> ReadAsync(string token, CancellationToken ct);

    /// <summary>Bytes da foto de perfil, ou nulo se ela nao veio.</summary>
    Task<byte[]?> ReadAvatarAsync(string url, CancellationToken ct);
}

/// <summary>
/// Pergunta ao GitHub de quem e o token guardado.
///
/// Serve a uma so coisa: a confirmacao visual de que a conexao e com a conta
/// certa. "Conectado" sozinho nao diz se o token e do time ou de uma conta
/// pessoal esquecida na maquina — a foto e o @ dizem na hora.
///
/// Uma resposta 401 aqui e informacao, nao erro: quer dizer que o token que
/// esta no cofre nao vale mais, e a tela pode dizer isso antes de alguem
/// descobrir no meio de uma build.
/// </summary>
public sealed class GitHubProfile : IGitHubProfile
{
    private const string UserEndpoint = "https://api.github.com/user";

    private readonly HttpClient _http;

    public GitHubProfile(HttpClient http)
    {
        _http = http;

        // A API do GitHub recusa pedido sem User-Agent.
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("UnityLocalCI");
    }

    public async Task<GitHubAccount?> ReadAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        using var pedido = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        pedido.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resposta = await _http.SendAsync(pedido, ct).ConfigureAwait(false);
        if (!resposta.IsSuccessStatusCode) return null;

        var corpo = await resposta.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            using var json = JsonDocument.Parse(corpo);
            var raiz = json.RootElement;

            var login = Texto(raiz, "login");
            if (login is null) return null;

            return new GitHubAccount(login, Texto(raiz, "name"), Texto(raiz, "avatar_url"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<byte[]?> ReadAvatarAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            // A foto e publica: o token nao vai junto de proposito.
            using var resposta = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resposta.IsSuccessStatusCode) return null;

            return await resposta.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static string? Texto(JsonElement raiz, string campo)
        => raiz.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;
}
