using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Publishing;

/// <summary>Uma linha do arquivo consolidado, antes de virar JSON.</summary>
public sealed record GlobalStatusRow(
    string Project,
    bool IsRunning,
    DateTimeOffset? RunningSince,
    BuildRecord? LastFinished);

/// <summary>
/// Como um estado e uma duracao viram texto para gente ler.
///
/// Esta classe ja montou os tres arquivos de status, alinhados a coluna, para
/// serem lidos de relance numa pasta compartilhada. Nao monta mais: os arquivos
/// viraram JSON (ver <see cref="StatusDocuments"/>) e a janela do BuildMaker
/// virou o lugar onde se olha. O que sobrou aqui e o vocabulario — e ele
/// sobrevive justamente por ser compartilhado entre o JSON, a grade de builds e
/// a notificacao do Teams, que precisam dizer a mesma coisa do mesmo jeito.
/// </summary>
public static class StatusFormatter
{
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
}
