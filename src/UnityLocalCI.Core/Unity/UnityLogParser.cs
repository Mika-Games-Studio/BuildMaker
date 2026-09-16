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
    private const int MaxCollected = 20;
    private const int MaxSummaryLines = 10;

    /// <summary>Prefixo com que o Builder.cs marca as linhas destinadas ao pipeline.</summary>
    public const string Marker = "[UnityLocalCI]";

    /// <summary>Erro ja curado pelo Builder.cs. Tem prioridade no resumo.</summary>
    public const string MarkedErrorPrefix = Marker + " ERRO:";

    /// <summary>Aviso destacado pelo Builder.cs, como a compressao Brotli sem fallback.</summary>
    public const string MarkedWarningPrefix = Marker + " AVISO:";

    public static UnityLogSummary Parse(IEnumerable<string> lines)
    {
        var markedErrors = new List<string>();
        var compilationErrors = new List<string>();
        var warnings = new List<string>();
        var generalErrors = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            // Os marcadores vem antes dos padroes genericos: o Builder ja decidiu
            // se a linha e erro ou aviso, e adivinhar de novo so erraria.
            if (line.Contains(MarkedErrorPrefix, StringComparison.Ordinal))
            {
                Collect(markedErrors, line);
                continue;
            }

            if (line.Contains(MarkedWarningPrefix, StringComparison.Ordinal))
            {
                Collect(warnings, line);
                continue;
            }

            // Demais linhas marcadas sao informativas: nao sao erro nem aviso.
            if (line.Contains(Marker, StringComparison.Ordinal)) continue;

            if (CompilationErrorPattern().IsMatch(line))
            {
                Collect(compilationErrors, line);
                continue;
            }

            if (CompilationWarningPattern().IsMatch(line))
            {
                Collect(warnings, line);
                continue;
            }

            if (GeneralErrorPattern().IsMatch(line))
                Collect(generalErrors, line);
        }

        // Prioridade do resumo: o que o Builder curou, depois o compilador,
        // depois qualquer erro solto que tenha sobrado.
        var summarySource =
            markedErrors.Count > 0 ? markedErrors :
            compilationErrors.Count > 0 ? compilationErrors :
            generalErrors;

        var summary = summarySource.Count == 0
            ? null
            : string.Join(Environment.NewLine, summarySource.Take(MaxSummaryLines));

        return new UnityLogSummary(markedErrors, compilationErrors, warnings, generalErrors, summary);
    }

    public static UnityLogSummary ParseText(string text)
        => Parse(text.Split('\n', StringSplitOptions.None));

    private static void Collect(List<string> target, string line)
    {
        if (target.Count >= MaxCollected || target.Contains(line)) return;
        target.Add(line);
    }

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
    IReadOnlyList<string> MarkedErrors,
    IReadOnlyList<string> CompilationErrors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> GeneralErrors,
    string? Summary)
{
    /// <summary>Todos os erros, do mais curado ao mais bruto.</summary>
    public IReadOnlyList<string> AllErrors =>
        MarkedErrors.Concat(CompilationErrors).Concat(GeneralErrors).ToList();
}
