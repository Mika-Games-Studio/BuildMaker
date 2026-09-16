using System.Text;
using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Abstractions;

namespace UnityLocalCI.Core.Unity;

/// <summary>
/// Invoca o Unity CLI ('unity build').
///
/// Desvio consciente da especificacao: ela previa argumentos custom depois de
/// '--' e '--format json' por comando. O CLI instalado (1.0.0-beta.5) nao tem
/// '--', repassa extras por '--args "&lt;string&gt;"' e encaminha '-o' ao editor
/// como '-buildOutput'. Seguimos o CLI real e mandamos o caminho de saida pelos
/// dois nomes, para o Builder.cs poder ler qualquer um.
/// </summary>
public sealed class UnityCliClient : IUnityCliClient
{
    private readonly IProcessRunner _runner;
    private readonly ILogger<UnityCliClient> _logger;

    public UnityCliClient(IProcessRunner runner, ILogger<UnityCliClient> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<UnityBuildResult> BuildAsync(
        UnityBuildRequest request, Action<string>? onLogLine, CancellationToken ct)
    {
        var editorLogPath = Path.ChangeExtension(request.LogFilePath, ".unity.log");
        Directory.CreateDirectory(Path.GetDirectoryName(request.LogFilePath)!);

        var args = new List<string>
        {
            "--non-interactive",
            "--no-banner",
            "build",
            request.ProjectPath,
            "--target", request.BuildTarget,
            "--execute-method", request.ExecuteMethod,
            "--editor-version", request.EditorVersion,
            "--output-path", request.OutputPath,
            "--log-file", editorLogPath,
            "--allow-install",
        };

        args.AddRange(request.ExtraArgs);

        var custom = BuildCustomArguments(request);
        if (custom.Length > 0)
        {
            args.Add("--args");
            args.Add(custom);
        }

        // Mantemos o tail (sem --no-tail) para que o log exista em tempo real:
        // se o build travar e for morto pelo timeout, o que ja saiu esta gravado.
        var capturedLines = new List<string>();

        var result = await _runner.RunAsync(
            new ProcessRequest
            {
                FileName = "unity",
                Arguments = args,
                WorkingDirectory = request.ProjectPath,
                Timeout = request.Timeout,
            },
            onOutput: (_, line) =>
            {
                capturedLines.Add(line);
                onLogLine?.Invoke(line);
            },
            ct).ConfigureAwait(false);

        // O log do editor costuma ter o erro de compilacao completo; o stdout do
        // CLI tem o resumo. Lemos os dois para nao depender de qual deles falou.
        var lines = new List<string>(capturedLines);
        lines.AddRange(ReadEditorLog(editorLogPath));

        var summary = UnityLogParser.Parse(lines);

        if (result.TimedOut)
        {
            _logger.LogError(
                "Build do Unity estourou o timeout de {Minutes} min e a arvore de processos foi encerrada.",
                request.Timeout.TotalMinutes);

            return new UnityBuildResult(
                ExitCode: -1,
                TimedOut: true,
                ErrorSummary: $"Build excedeu o timeout de {request.Timeout.TotalMinutes:0} minutos e foi encerrada.",
                CompilationErrors: summary.CompilationErrors,
                Warnings: summary.Warnings);
        }

        // Sucesso e exit code, e so. A pasta de saida pode existir cheia de
        // artefatos parciais mesmo quando o build falhou.
        var errorSummary = result.ExitCode == 0
            ? null
            : summary.Summary ?? $"Unity terminou com exit code {result.ExitCode} sem erro de compilacao identificavel no log.";

        return new UnityBuildResult(
            result.ExitCode,
            TimedOut: false,
            ErrorSummary: errorSummary,
            CompilationErrors: summary.CompilationErrors,
            Warnings: summary.Warnings);
    }

    private static string BuildCustomArguments(UnityBuildRequest request)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in request.CustomArguments)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(key.StartsWith('-') ? key : "-" + key);
            builder.Append(' ');
            builder.Append(value.Contains(' ') ? $"\"{value}\"" : value);
        }
        return builder.ToString();
    }

    private IReadOnlyList<string> ReadEditorLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel ler o log do editor em {Path}.", path);
            return Array.Empty<string>();
        }
    }
}
