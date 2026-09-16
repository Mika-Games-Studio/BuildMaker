using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Publishing;

namespace UnityLocalCI.Core.Watching;

/// <summary>
/// Ouvinte em loopback, e so em loopback.
///
/// Atende duas coisas:
///   POST /                              sinal do hook post-merge
///   POST /api/builds/{id}/republish     reenvio de artefato pendente
///
/// A decisao de nao ter painel nem API continua valendo: isto nao e um servidor
/// web. O prefixo e http://127.0.0.1, entao nada fora da maquina alcanca a
/// porta, nao ha hostname para configurar e nao ha firewall para liberar. O
/// sinal do hook e previsto na secao 5.1 da especificacao, e o hook nunca
/// enfileira direto: ele so antecipa a verificacao do watcher.
/// </summary>
public sealed partial class SignalListener : BackgroundService
{
    private readonly GitWatcherRegistry _watchers;
    private readonly IPendingCopyService _pending;
    private readonly IOptions<CiOptions> _options;
    private readonly ILogger<SignalListener> _logger;

    public SignalListener(
        GitWatcherRegistry watchers,
        IPendingCopyService pending,
        IOptions<CiOptions> options,
        ILogger<SignalListener> logger)
    {
        _watchers = watchers;
        _pending = pending;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = ResolvePort();
        if (port is null)
        {
            _logger.LogInformation("Nenhuma HookSignalPort configurada; o sinal em loopback esta desligado.");
            return;
        }

        using var listener = new HttpListener();

        // 127.0.0.1 e nao localhost nem +: o primeiro amarra so a interface de
        // loopback; os outros aceitariam conexao de fora da maquina.
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException exception)
        {
            _logger.LogWarning(
                "Nao foi possivel abrir a porta {Port} em loopback: {Message}. O polling continua funcionando normalmente.",
                port, exception.Message);
            return;
        }

        _logger.LogInformation("Sinal em loopback ouvindo em http://127.0.0.1:{Port}/.", port);

        using var registration = stoppingToken.Register(listener.Stop);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException exception)
            {
                _logger.LogWarning("Falha ao aceitar conexao em loopback: {Message}", exception.Message);
                continue;
            }

            // Uma requisicao lenta nunca pode segurar as demais, nem derrubar o
            // ouvinte: cada uma e tratada por conta propria.
            _ = HandleSafelyAsync(context, stoppingToken);
        }
    }

    private int? ResolvePort()
    {
        var projects = ProjectResolver.ResolveEnabled(_options.Value);
        if (projects.Count == 0) return null;

        var ports = projects.Select(p => p.Watcher.HookSignalPort).Distinct().ToArray();
        if (ports.Length > 1)
        {
            _logger.LogWarning(
                "Ha mais de uma HookSignalPort configurada ({Ports}); usando {Chosen}. " +
                "O ouvinte e um so para a maquina inteira e o projeto vai no corpo do POST.",
                string.Join(", ", ports), ports[0]);
        }

        return ports[0];
    }

    private async Task HandleSafelyAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            await HandleAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Erro ao tratar requisicao em loopback.");
            TryRespond(context, 500, "erro interno");
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;

        // Cinto e suspensorio: o prefixo ja e de loopback, mas negar
        // explicitamente torna a intencao impossivel de perder numa refatoracao.
        if (request.RemoteEndPoint is not null && !IPAddress.IsLoopback(request.RemoteEndPoint.Address))
        {
            Respond(context, 403, "apenas loopback");
            return;
        }

        var path = request.Url?.AbsolutePath ?? "/";
        var isPost = string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);

        var republish = RepublishPattern().Match(path);
        if (republish.Success)
        {
            // Reenviar tem efeito, entao continua so por POST.
            if (!isPost) { Respond(context, 405, "use POST"); return; }

            await HandleRepublishAsync(context, long.Parse(republish.Groups[1].Value), ct).ConfigureAwait(false);
            return;
        }

        if (path is "/" or "/signal")
        {
            // GET tambem serve aqui. O http.sys responde 411 a um POST sem
            // Content-Length antes de a requisicao chegar neste codigo, e a
            // especificacao descreve o hook como "um POST vazio": sem o GET,
            // 'curl -X POST' sem corpo nunca funcionaria. O sinal e idempotente
            // e so antecipa uma verificacao, entao aceitar GET nao abre nada.
            if (!isPost && !string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                Respond(context, 405, "use POST ou GET");
                return;
            }

            await HandleSignalAsync(context, request, ct).ConfigureAwait(false);
            return;
        }

        Respond(context, 404, "rota desconhecida");
    }

    private async Task HandleSignalAsync(HttpListenerContext context, HttpListenerRequest request, CancellationToken ct)
    {
        var project = (await ReadBodyAsync(request, ct).ConfigureAwait(false)).Trim();

        if (string.IsNullOrEmpty(project))
        {
            var count = _watchers.PokeAll();
            _logger.LogInformation("Sinal do hook sem projeto no corpo; {Count} watcher(s) antecipados.", count);
            Respond(context, 200, $"{count} projeto(s) verificando agora");
            return;
        }

        if (_watchers.Poke(project))
        {
            _logger.LogInformation("Sinal do hook para {Project}; verificacao antecipada.", project);
            Respond(context, 200, $"{project} verificando agora");
            return;
        }

        _logger.LogWarning("Sinal do hook para projeto desconhecido: {Project}", project);
        Respond(context, 404, $"projeto '{project}' nao esta configurado");
    }

    private async Task HandleRepublishAsync(HttpListenerContext context, long buildId, CancellationToken ct)
    {
        var error = await _pending.RetryAsync(buildId, ct).ConfigureAwait(false);

        if (error is null)
        {
            Respond(context, 200, $"build {buildId} reenviada");
            return;
        }

        Respond(context, 409, error);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (!request.HasEntityBody) return string.Empty;

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static void Respond(HttpListenerContext context, int status, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message + Environment.NewLine);

        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static void TryRespond(HttpListenerContext context, int status, string message)
    {
        try { Respond(context, status, message); } catch (Exception) { /* conexao ja fechada */ }
    }

    [GeneratedRegex(@"^/api/builds/(\d+)/republish/?$", RegexOptions.IgnoreCase)]
    private static partial Regex RepublishPattern();
}
