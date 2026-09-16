namespace UnityLocalCI.Core.Abstractions;

/// <summary>
/// Um processo a executar. <see cref="DisplayArguments"/> existe separado de
/// <see cref="Arguments"/> porque a linha de comando real pode conter um PAT
/// (git http.extraHeader) e nada disso pode chegar ao log.
/// </summary>
public sealed record ProcessRequest
{
    public required string FileName { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public TimeSpan? Timeout { get; init; }

    /// <summary>Versao segura para log. Quando nula, os argumentos reais sao usados.</summary>
    public IReadOnlyList<string>? DisplayArguments { get; init; }

    public string SafeCommandLine =>
        $"{FileName} {string.Join(' ', (DisplayArguments ?? Arguments).Select(Quote))}";

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}

public sealed record ProcessResult(int ExitCode, bool TimedOut, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

public enum OutputStream { StandardOutput, StandardError }

public interface IProcessRunner
{
    /// <summary>
    /// Executa e aguarda. <paramref name="onOutput"/> recebe cada linha em tempo real,
    /// para que o log da build exista mesmo se o processo travar depois.
    /// Ao estourar o timeout, a arvore inteira de processos e encerrada.
    /// </summary>
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Action<OutputStream, string>? onOutput,
        CancellationToken ct);
}
