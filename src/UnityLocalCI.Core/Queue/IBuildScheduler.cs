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
    /// Cancela uma build, esteja ela correndo ou esperando na fila. Verdadeiro
    /// se ela existia num dos dois lugares.
    ///
    /// Em execucao, o Unity morre junto com os filhos — o pipeline trata o
    /// cancelamento como encerramento normal, entao nao sobra processo segurando
    /// a Library. Na fila, ela simplesmente nao chega a comecar.
    ///
    /// Nos dois casos a build fica no historico como Cancelada: ela foi pedida,
    /// e some-la sem deixar rastro esconderia que alguem mandou parar.
    /// </summary>
    Task<bool> CancelAsync(long buildId, CancellationToken ct);

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
