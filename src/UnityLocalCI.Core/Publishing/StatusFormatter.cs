using System.Text;
using UnityLocalCI.Core.Pipeline;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Publishing;

/// <summary>Uma linha do _STATUS-GERAL.txt.</summary>
public sealed record GlobalStatusRow(
    string Project,
    bool IsRunning,
    DateTimeOffset? RunningSince,
    BuildRecord? LastFinished);

/// <summary>
/// Formatacao dos arquivos que o time le na pasta de destino.
///
/// Sem painel web, a pasta e a interface: estes tres arquivos sao o que evita a
/// pergunta "a build saiu?" no chat. Tudo aqui e funcao pura, para que o formato
/// seja fixado por teste em vez de conferido a olho.
/// </summary>
public static class StatusFormatter
{
    /// <summary>Quantas builds o _HISTORICO.txt guarda.</summary>
    public const int HistoryLength = 20;

    private const string DateTimeFormat = "dd/MM/yyyy HH:mm";
    private const string ShortDateTimeFormat = "dd/MM HH:mm";

    // ------------------------------------------------------------ _STATUS.txt

    public static string FormatProjectStatus(
        BuildRecord current,
        BuildRecord? previous,
        IReadOnlyList<string> warnings,
        string? playHint)
    {
        var text = new StringBuilder();

        text.AppendLine("ÚLTIMA BUILD: " + Label(current.Status));
        text.AppendLine(Field("Data", Local(current.FinishedAt ?? current.StartedAt ?? current.QueuedAt)
            .ToString(DateTimeFormat)));
        text.AppendLine(Field("Commit", current.ShortSha + "  \"" + (current.CommitMessage ?? "(sem mensagem)") + "\""));
        text.AppendLine(Field("Autor", current.CommitAuthor ?? "(desconhecido)"));
        text.AppendLine(Field("Duração", FormatDuration(current.DurationSeconds)));

        if (current.Status == BuildStatus.Succeeded)
        {
            text.AppendLine(Field("Arquivo", ArtifactDescription(current)));
            if (!string.IsNullOrWhiteSpace(playHint))
                text.AppendLine(Field("Jogar", playHint!));
        }
        else
        {
            foreach (var line in ErrorLines(current.ErrorSummary))
                text.AppendLine(line);
        }

        if (current.LogPath is not null)
            text.AppendLine(Field("Log", LogReference(current)));

        if (warnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("ATENÇÃO:");
            foreach (var warning in warnings)
                text.AppendLine("  - " + Collapse(warning));
        }

        if (previous is not null)
        {
            text.AppendLine();
            text.AppendLine("Build anterior: " +
                            Local(previous.FinishedAt ?? previous.QueuedAt).ToString(DateTimeFormat) +
                            "  " + Label(previous.Status));
        }

        return text.ToString();
    }

    /// <summary>
    /// O erro vem do resumo do log e pode ter varias linhas. A primeira fica no
    /// campo alinhado; as demais entram indentadas, para o bloco nao virar uma
    /// linha unica gigante.
    /// </summary>
    private static IEnumerable<string> ErrorLines(string? errorSummary)
    {
        if (string.IsNullOrWhiteSpace(errorSummary))
        {
            yield return Field("Erro", "(sem detalhe no log)");
            yield break;
        }

        var lines = errorSummary
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        yield return Field("Erro", lines[0]);

        foreach (var extra in lines.Skip(1))
            yield return "            " + extra;
    }

    // --------------------------------------------------------- _HISTORICO.txt

    /// <summary>Uma linha por build, da mais antiga para a mais recente.</summary>
    public static string FormatHistory(IEnumerable<BuildRecord> chronological)
    {
        var text = new StringBuilder();
        foreach (var build in chronological)
        {
            text.Append(Local(build.FinishedAt ?? build.QueuedAt).ToString(DateTimeFormat));
            text.Append("  ");
            text.Append(Label(build.Status).PadRight(12));
            text.Append(FormatDuration(build.DurationSeconds).PadLeft(12));
            text.Append("  ");
            text.Append(build.ShortSha.PadRight(7));
            text.Append("  ");
            text.Append(Truncate(build.CommitAuthor ?? "(desconhecido)", 22).PadRight(22));
            text.Append("  ");
            text.AppendLine(build.Status == BuildStatus.Succeeded
                ? ArtifactName(build) ?? "—"
                : LogReference(build));
        }

        return text.ToString();
    }

    // ----------------------------------------------------- _STATUS-GERAL.txt

    /// <summary>
    /// Uma linha por projeto. Existe porque, com varios projetos, abrir N pastas
    /// para saber o que esta acontecendo e o principal incomodo de nao haver
    /// interface.
    /// </summary>
    public static string FormatGlobalStatus(
        DateTimeOffset now,
        IReadOnlyList<GlobalStatusRow> rows,
        int waiting,
        int running,
        int maxConcurrentBuilds)
    {
        var cells = rows.Select(BuildCells).ToList();

        var projectWidth = Width(cells.Select(c => c.Project), "PROJETO");
        var stateWidth = Width(cells.Select(c => c.State), "ESTADO");
        var whenWidth = Width(cells.Select(c => c.When), "ÚLTIMA BUILD");
        var shaWidth = Width(cells.Select(c => c.Sha), "COMMIT");

        var text = new StringBuilder();
        text.AppendLine("CI LOCAL — " + Local(now).ToString(DateTimeFormat));
        text.AppendLine();

        text.AppendLine(Row("PROJETO", "ESTADO", "ÚLTIMA BUILD", "COMMIT", "ARQUIVO",
            projectWidth, stateWidth, whenWidth, shaWidth));

        foreach (var cell in cells)
        {
            text.AppendLine(Row(cell.Project, cell.State, cell.When, cell.Sha, cell.Artifact,
                projectWidth, stateWidth, whenWidth, shaWidth));
        }

        text.AppendLine();
        text.AppendLine($"Fila: {waiting} aguardando  |  Em execução: {running} de {maxConcurrentBuilds}");

        return text.ToString();
    }

    private static (string Project, string State, string When, string Sha, string Artifact) BuildCells(GlobalStatusRow row)
    {
        if (row.IsRunning)
        {
            var since = row.RunningSince is { } start
                ? "(iniciou " + Local(start).ToString("HH:mm") + ")"
                : "(em execução)";

            return (row.Project, "construindo", since, ShaOf(row.LastFinished), "—");
        }

        if (row.LastFinished is not { } last)
            return (row.Project, "—", "nunca", "—", "—");

        var when = Local(last.FinishedAt ?? last.QueuedAt).ToString(ShortDateTimeFormat);

        // Minusculo para o que esta bem, maiusculo para o que precisa de atencao:
        // a coluna inteira e lida de relance, e o que grita e o que importa.
        return last.Status switch
        {
            BuildStatus.Succeeded => (row.Project, "ok", when, last.ShortSha, ArtifactName(last) ?? "—"),
            BuildStatus.Failed => (row.Project, "FALHOU", when, last.ShortSha, LogReference(last)),
            BuildStatus.Interrupted => (row.Project, "INTERROMPIDA", when, last.ShortSha, LogReference(last)),
            _ => (row.Project, Label(last.Status), when, last.ShortSha, "—"),
        };
    }

    private static string Row(
        string project, string state, string when, string sha, string artifact,
        int projectWidth, int stateWidth, int whenWidth, int shaWidth)
        => (project.PadRight(projectWidth) + "  " +
            state.PadRight(stateWidth) + "  " +
            when.PadRight(whenWidth) + "  " +
            sha.PadRight(shaWidth) + "  " +
            artifact).TrimEnd();

    private static int Width(IEnumerable<string> values, string header)
        => Math.Max(header.Length, values.Select(v => v.Length).DefaultIfEmpty(0).Max());

    // ------------------------------------------------------------ utilidades

    public static string Label(BuildStatus status) => status switch
    {
        BuildStatus.Succeeded => "SUCESSO",
        BuildStatus.Failed => "FALHOU",
        BuildStatus.Cancelled => "CANCELADA",
        BuildStatus.Interrupted => "INTERROMPIDA",
        BuildStatus.Running => "EM EXECUÇÃO",
        _ => "NA FILA",
    };

    public static string FormatDuration(int? seconds)
    {
        if (seconds is not { } total || total < 0) return "—";
        if (total < 60) return total + " s";

        var minutes = total / 60;
        var rest = total % 60;

        if (minutes < 60) return minutes + " min " + rest.ToString("00") + " s";

        var hours = minutes / 60;
        return hours + " h " + (minutes % 60).ToString("00") + " min";
    }

    private static string Field(string name, string value)
        => name.PadRight(10, '.') + ": " + value;

    private static string ArtifactDescription(BuildRecord build)
    {
        var name = ArtifactName(build);
        if (name is null) return "—";

        return build.ArtifactSizeBytes is { } size
            ? name + "  (" + PackageStep.FormatSize(size) + ")"
            : name;
    }

    private static string? ArtifactName(BuildRecord build)
    {
        var path = build.PublishedPath ?? build.ArtifactPath;
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);
    }

    /// <summary>Caminho relativo do log dentro da pasta de destino, que e onde o time o encontra.</summary>
    private static string LogReference(BuildRecord build)
        => @"ver _logs\build-" + build.Id + ".log";

    private static string ShaOf(BuildRecord? build) => build?.ShortSha ?? "—";

    private static DateTimeOffset Local(DateTimeOffset value) => value.ToLocalTime();

    private static string Collapse(string text)
        => string.Join(' ', text.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0));

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";
}
