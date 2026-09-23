using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;

namespace UnityLocalCI.Core.Queue;

public interface IBuildScheduler
{
    /// <summary>
    /// Cria o registro da build e a coloca na fila do projeto. Retorna o BuildId,
    /// ou nulo se o projeto nao estiver registrado no scheduler.
    /// </summary>
    Task<long?> EnqueueAsync(ResolvedProject project, CommitInfo commit, BuildTrigger trigger, CancellationToken ct);

    SchedulerSnapshot Snapshot();

    /// <summary>
    /// Encerra a build em andamento. Verdadeiro se ela existia e foi avisada.
    ///
    /// O Unity morre junto, com os filhos: o pipeline ja trata o cancelamento
    /// como um encerramento normal, entao o workspace nao fica com processo
    /// solto segurando a Library.
    /// </summary>
    bool Cancel(long buildId);

    /// <summary>
    /// Segura a fila. A build que ja esta correndo segue ate o fim — Unity no
    /// meio de uma importacao nao se congela sem estragar o cache —, mas
    /// nenhuma outra comeca enquanto isto estiver ligado.
    /// </summary>
    bool Paused { get; set; }
}

/// <param name="Paused">A fila esta segurando os proximos jobs.</param>
public sealed record SchedulerSnapshot(int Waiting, int Running, int MaxConcurrentBuilds, bool Paused = false);

/// <summary>Executa uma build. Implementado pelo pipeline; separado para o scheduler ser testavel sozinho.</summary>
public interface IBuildRunner
{
    Task RunAsync(BuildJob job, CancellationToken ct);
}
