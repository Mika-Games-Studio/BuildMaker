using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Pipeline;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.Secrets;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Notifications;

/// <summary>
/// Notificacao no Teams por webhook. Opcional: sem
/// Notifications.TeamsWebhookCredentialName configurado, nao faz nada.
///
/// A URL do webhook e um segredo como qualquer outro e vive no Credential
/// Manager: quem tem a URL pode postar no canal.
/// </summary>
public sealed class TeamsNotifier : INotifier
{
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;
    private readonly IOptionsMonitor<CiOptions> _options;
    private readonly ILogger<TeamsNotifier> _logger;

    public TeamsNotifier(
        HttpClient http,
        ICredentialStore credentials,
        IOptionsMonitor<CiOptions> options,
        ILogger<TeamsNotifier> logger)
    {
        _http = http;
        _credentials = credentials;
        _options = options;
        _logger = logger;
    }

    public async Task NotifyAsync(BuildRecord build, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        var credentialName = _options.CurrentValue.Notifications.TeamsWebhookCredentialName;
        if (string.IsNullOrWhiteSpace(credentialName)) return;

        var url = _credentials.Read(credentialName!);
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning(
                "Credencial {Credential} do webhook do Teams nao existe; a notificacao foi pulada.", credentialName);
            return;
        }

        var payload = BuildPayload(build, warnings);

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "O Teams recusou a notificacao da build {BuildId} com status {Status}.",
                    build.Id, (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Notificacao e conveniencia: nunca pode mudar o resultado de uma
            // build que ja terminou.
            _logger.LogWarning(
                "Nao foi possivel notificar o Teams sobre a build {BuildId}: {Message}", build.Id, exception.Message);
        }
    }

    /// <summary>
    /// Adaptive Card no envelope que os fluxos do Power Automate esperam, que e
    /// o caminho atual dos webhooks de Teams. Se o canal ainda usar um connector
    /// antigo do Office 365, a URL espera o formato MessageCard; e so trocar o
    /// corpo desta funcao, que e o unico lugar que conhece o formato.
    /// </summary>
    internal static string BuildPayload(BuildRecord build, IReadOnlyList<string> warnings)
    {
        var success = build.Status == BuildStatus.Succeeded;

        var facts = new List<object>
        {
            Fact("Projeto", build.Project),
            Fact("Resultado", StatusFormatter.Label(build.Status)),
            Fact("Commit", $"{build.ShortSha}  {build.CommitMessage ?? "(sem mensagem)"}"),
            Fact("Autor", build.CommitAuthor ?? "(desconhecido)"),
            Fact("Duração", StatusFormatter.FormatDuration(build.DurationSeconds)),
        };

        if (success && build.ArtifactSizeBytes is { } size)
            facts.Add(Fact("Tamanho", PackageStep.FormatSize(size)));

        var location = build.PublishedPath ?? build.ArtifactPath;
        if (location is not null)
            facts.Add(Fact(success ? "Arquivo" : "Artefato", location));

        if (!success && build.ErrorSummary is { } error)
            facts.Add(Fact("Erro", FirstLine(error)));

        var body = new List<object>
        {
            new
            {
                type = "TextBlock",
                size = "Medium",
                weight = "Bolder",
                wrap = true,
                color = success ? "Good" : "Attention",
                text = $"{(success ? "Build OK" : "Build falhou")} — {build.Project}",
            },
            new { type = "FactSet", facts },
        };

        foreach (var warning in warnings.Take(5))
        {
            body.Add(new
            {
                type = "TextBlock",
                wrap = true,
                color = "Warning",
                text = "⚠ " + FirstLine(warning),
            });
        }

        var card = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        schema = "http://adaptivecards.io/schemas/adaptive-card.json",
                        version = "1.4",
                        body,
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(card);

        // O campo do schema se chama "$schema" no Adaptive Card, e "$" nao e um
        // identificador valido em C#.
        return json.Replace("\"schema\":", "\"$schema\":", StringComparison.Ordinal);
    }

    private static object Fact(string title, string value) => new { title, value };

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return (index >= 0 ? text[..index] : text).Trim();
    }
}
