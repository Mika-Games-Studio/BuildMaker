using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
/// Depois disso, o commit interrompido volta para a fila — com teto.
///
/// Esta classe ja reenfileirou incondicionalmente, e isso virava ciclo: sobe,
/// comeca a mesma build de quinze minutos, cai, sobe, comeca de novo. Foi
/// removido, e o preco de remover apareceu em uso: como o last_built_sha e
/// gravado no momento do enfileiramento, um commit cuja build morreu no meio
/// ficava marcado como construido para sempre. O merge sumia sem aviso, e a
/// unica saida era alguem reparar e clicar em 'Construir agora'.
///
/// O teto — <see cref="SchedulerOptions.MaxInterruptedRetries"/> — e o que
/// separa uma coisa da outra: o commit volta no maximo N vezes e depois para.
/// Zero devolve o comportamento anterior.
///
/// Uma protecao vem de graca: se a retentativa esbarrar numa Library do Unity
/// envenenada por uma importacao morta pela metade, ela termina como FALHOU, e
/// FALHOU nao e reenfileirado. O ciclo nao se forma nem com o teto alto.
/// </summary>
public sealed class OrphanRecovery
{
    private readonly IBuildStore _store;
    private readonly IOptions<CiOptions> _options;
    private readonly IClock _clock;
    private readonly ILogger<OrphanRecovery> _logger;

    public OrphanRecovery(
        IBuildStore store,
        IOptions<CiOptions> options,
        IClock clock,
        ILogger<OrphanRecovery> logger)
    {
        _store = store;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task RecoverAsync(IReadOnlyList<ResolvedProject> projects, CancellationToken ct)
    {
        await FecharAsync(
            BuildStatus.Running,
            BuildStatus.Interrupted,
            "Interrompida: o servico parou no meio dela.",
            "{Count} build(s) estavam em execucao quando o servico parou. Marcando como Interrompida.",
            ct).ConfigureAwait(false);

        await FecharAsync(
            BuildStatus.Queued,
            BuildStatus.Cancelled,
            "Cancelada: o servico parou antes de ela comecar.",
            "{Count} build(s) esperavam na fila quando o servico parou. Marcando como Cancelada.",
            ct).ConfigureAwait(false);

        await ReenfileirarInterrompidasAsync(projects, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Devolve a vez ao commit cuja ultima build foi interrompida.
    ///
    /// Nao enfileira nada aqui: apaga o last_built_sha, e o watcher faz o resto
    /// no ciclo seguinte. Isso mantem uma decisao so num lugar so — e de quebra
    /// a build volta depois da janela de debounce, e nao no primeiro segundo
    /// depois do boot, que e justamente quando a maquina esta mais ocupada.
    /// </summary>
    private async Task ReenfileirarInterrompidasAsync(
        IReadOnlyList<ResolvedProject> projects, CancellationToken ct)
    {
        var teto = _options.Value.Scheduler.MaxInterruptedRetries;
        if (teto <= 0) return;

        // A retencao mantem poucas builds por projeto, entao carregar todas as
        // interrompidas de uma vez e mais barato que uma consulta por projeto.
        var interrompidas = await _store
            .GetByStatusAsync(BuildStatus.Interrupted, ct).ConfigureAwait(false);

        if (interrompidas.Count == 0) return;

        foreach (var projeto in projects)
        {
            var ultima = await _store.GetLastFinishedAsync(projeto.Name, ct).ConfigureAwait(false);

            // So a MAIS RECENTE conta. Se depois dela veio uma build que passou
            // ou falhou, aquele commit ja teve seu veredito e nao se mexe mais.
            if (ultima is null || ultima.Status != BuildStatus.Interrupted) continue;

            var marcado = await _store
                .GetWatcherValueAsync(projeto.Name, WatcherFields.LastBuiltSha, ct).ConfigureAwait(false);

            // Se o marcador ja aponta para outro commit, o watcher vai pegar
            // aquele sozinho: nao ha nada preso.
            if (!string.Equals(marcado, ultima.CommitSha, StringComparison.OrdinalIgnoreCase)) continue;

            // Se alguma build daquele commit ja passou, o artefato existe e nao
            // ha o que recuperar. Acontece quando alguem manda 'Construir agora'
            // num commit ja construido e ESSA build e que morre: a mais recente
            // e uma interrupcao, mas o trabalho nao se perdeu.
            if (await JaPassouAsync(projeto.Name, ultima.CommitSha, ct).ConfigureAwait(false)) continue;

            var tentativas = interrompidas.Count(b =>
                string.Equals(b.Project, projeto.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.CommitSha, ultima.CommitSha, StringComparison.OrdinalIgnoreCase));

            if (tentativas > teto)
            {
                _logger.LogWarning(
                    "[{Project}] o commit {Sha} foi interrompido {Count} vez(es) e nao sera refeito sozinho. " +
                    "Use 'Construir agora' quando quiser tentar de novo.",
                    projeto.Name, Curto(ultima.CommitSha), tentativas);
                continue;
            }

            await _store
                .SetWatcherValueAsync(projeto.Name, WatcherFields.LastBuiltSha, null, ct)
                .ConfigureAwait(false);

            _logger.LogWarning(
                "[{Project}] a build do commit {Sha} foi interrompida; ele volta para a fila " +
                "(tentativa {Count} de {Max}).",
                projeto.Name, Curto(ultima.CommitSha), tentativas, teto + 1);
        }
    }

    private async Task<bool> JaPassouAsync(string projeto, string sha, CancellationToken ct)
    {
        var passadas = await _store.GetByStatusAsync(BuildStatus.Succeeded, ct).ConfigureAwait(false);

        return passadas.Any(b =>
            string.Equals(b.Project, projeto, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(b.CommitSha, sha, StringComparison.OrdinalIgnoreCase));
    }

    private static string Curto(string sha) => sha.Length >= 7 ? sha[..7] : sha;

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
