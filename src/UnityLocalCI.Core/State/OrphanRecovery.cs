using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;

namespace UnityLocalCI.Core.State;

/// <summary>
/// Fecha, na inicializacao, os registros que ficaram abertos no banco.
///
/// A fila vive na memoria do processo. Quando ele para — de proposito ou nao —,
/// tudo que estava em execucao ou esperando a vez deixa de existir, mas o
/// registro no banco continua dizendo Running ou Queued. Sao registros que nunca
/// mais se fecham sozinhos: nao ha quem os termine, nao aparecem como concluidos
/// e nao podem ser apagados do historico, porque apagar uma build viva seria
/// apagar algo que o pipeline ainda vai escrever. Ficam na lista para sempre.
///
/// Aqui eles sao fechados: o que estava correndo vira Interrompida, o que estava
/// na fila vira Cancelada.
///
/// Esta classe ja reenfileirava sozinha o commit interrompido quando ele ainda
/// era o HEAD. Nao faz mais. A recuperacao automatica transformava qualquer
/// encerramento no meio de uma build num ciclo: sobe, comeca a mesma build de
/// quinze minutos, cai, sobe, comeca de novo — e uma importacao do Unity morta
/// pela metade ainda envenenava a Library para a proxima. Quem decide refazer a
/// build agora e quem esta na janela, com 'Construir agora'.
/// </summary>
public sealed class OrphanRecovery
{
    private readonly IBuildStore _store;
    private readonly IClock _clock;
    private readonly ILogger<OrphanRecovery> _logger;

    public OrphanRecovery(IBuildStore store, IClock clock, ILogger<OrphanRecovery> logger)
    {
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    /// <param name="projects">
    /// Nao e mais usado para decidir nada — fica na assinatura porque o
    /// <see cref="Hosting.StartupService"/> ja tem a lista em maos e porque o
    /// numero de projetos habilitados aparece no aviso.
    /// </param>
    public async Task RecoverAsync(IReadOnlyList<ResolvedProject> projects, CancellationToken ct)
    {
        _ = projects;

        await FecharAsync(
            BuildStatus.Running,
            BuildStatus.Interrupted,
            "Interrompida: o servico parou no meio dela.",
            "{Count} build(s) estavam em execucao quando o servico parou. Marcando como Interrompida; " +
            "nenhuma e refeita sozinha.",
            ct).ConfigureAwait(false);

        await FecharAsync(
            BuildStatus.Queued,
            BuildStatus.Cancelled,
            "Cancelada: o servico parou antes de ela comecar.",
            "{Count} build(s) esperavam na fila quando o servico parou. Marcando como Cancelada.",
            ct).ConfigureAwait(false);
    }

    private async Task FecharAsync(
        BuildStatus aberto,
        BuildStatus fechado,
        string motivo,
        string aviso,
        CancellationToken ct)
    {
        var orfas = await _store.GetByStatusAsync(aberto, ct).ConfigureAwait(false);
        if (orfas.Count == 0) return;

        _logger.LogWarning(aviso, orfas.Count);

        foreach (var orfa in orfas)
        {
            await _store.UpdateAsync(orfa with
            {
                Status = fechado,
                FinishedAt = orfa.FinishedAt ?? _clock.UtcNow,
                ErrorSummary = motivo,
            }, ct).ConfigureAwait(false);
        }
    }
}
