using System.Text.RegularExpressions;

namespace UnityLocalCI.Core.Unity;

/// <summary>
/// Extrai o que interessa de um log do Unity.
///
/// O log tem dezenas de milhares de linhas e o erro de compilacao fica enterrado
/// no meio. Quem abre o _STATUS.txt precisa ver o erro nas primeiras linhas, nao
/// procurar por ele.
/// </summary>
public static partial class UnityLogParser
{
    private const int MaxCompilationErrors = 20;
    private const int MaxSummaryLines = 10;

    /// <summary>Prefixo com que o Builder.cs marca avisos destinados ao _STATUS.txt.</summary>
    public const string HighlightedWarningPrefix = "[UnityLocalCI]";

    public static UnityLogSummary Parse(IEnumerable<string> lines)
    {
        var compilationErrors = new List<string>();
        var warnings = new List<string>();
        var generalErrors = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            if (CompilationErrorPattern().IsMatch(line))
            {
                if (!compilationErrors.Contains(line) && compilationErrors.Count < MaxCompilationErrors)
                    compilationErrors.Add(line);
                continue;
            }

            // Avisos que o Builder.cs marca para o pipeline propagar, como a
            // combinacao Brotli sem decompressionFallback.
            if (line.Contains(HighlightedWarningPrefix, StringComparison.Ordinal))
            {
                if (!warnings.Contains(line)) warnings.Add(line);
                continue;
            }

            if (CompilationWarningPattern().IsMatch(line))
            {
                if (!warnings.Contains(line) && warnings.Count < MaxCompilationErrors)
                    warnings.Add(line);
                continue;
            }

            if (GeneralErrorPattern().IsMatch(line) && generalErrors.Count < MaxCompilationErrors)
                generalErrors.Add(line);
        }

        var summarySource = compilationErrors.Count > 0 ? compilationErrors : generalErrors;
        var summary = summarySource.Count == 0
            ? null
            : string.Join(Environment.NewLine, summarySource.Take(MaxSummaryLines));

        return new UnityLogSummary(compilationErrors, warnings, generalErrors, summary);
    }

    public static UnityLogSummary ParseText(string text)
        => Parse(text.Split('\n', StringSplitOptions.None));

    // "Assets/Scripts/Player.cs(12,9): error CS0103: ..."
    [GeneratedRegex(@"^.+\(\d+,\d+\):\s*error\s+\w+\d*:", RegexOptions.IgnoreCase)]
    private static partial Regex CompilationErrorPattern();

    [GeneratedRegex(@"^.+\(\d+,\d+\):\s*warning\s+\w+\d*:", RegexOptions.IgnoreCase)]
    private static partial Regex CompilationWarningPattern();

    // Erros que nao vem do compilador: o proprio CLI, Emscripten, IL2CPP, build report.
    [GeneratedRegex(@"(^\s*(Error|Fatal)\s*:|BuildFailedException|Build completed with a result of 'Failed'|^Error building Player|UnityEditor\.BuildPlayerWindow.+BuildMethodException|emcc:\s*error|error:\s*undefined symbol|IL2CPP error)", RegexOptions.IgnoreCase)]
    private static partial Regex GeneralErrorPattern();
}

public sealed record UnityLogSummary(
    IReadOnlyList<string> CompilationErrors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> GeneralErrors,
    string? Summary);
