namespace UnityLocalCI.Core.Unity;

public sealed record UnityBuildRequest
{
    public required string ProjectPath { get; init; }
    public required string EditorVersion { get; init; }
    public required string BuildTarget { get; init; }
    public required string ExecuteMethod { get; init; }
    public required string OutputPath { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required IReadOnlyList<string> ExtraArgs { get; init; }

    /// <summary>Argumentos -ci* repassados ao Builder.cs.</summary>
    public required IReadOnlyDictionary<string, string> CustomArguments { get; init; }
}

/// <summary>
/// Resultado bruto de uma invocacao do Unity. Sucesso vem do exit code, nunca
/// da existencia da pasta de saida: o Unity deixa artefatos parciais em disco
/// mesmo quando falha.
/// </summary>
public sealed record UnityBuildResult(
    int ExitCode,
    bool TimedOut,
    string? ErrorSummary,
    IReadOnlyList<string> CompilationErrors,
    IReadOnlyList<string> Warnings)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public interface IUnityCliClient
{
    Task<UnityBuildResult> BuildAsync(UnityBuildRequest request, Action<string>? onLogLine, CancellationToken ct);
}
