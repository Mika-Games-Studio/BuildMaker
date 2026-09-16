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
}

public sealed record SchedulerSnapshot(int Waiting, int Running, int MaxConcurrentBuilds);

/// <summary>Executa uma build. Implementado pelo pipeline; separado para o scheduler ser testavel sozinho.</summary>
public interface IBuildRunner
{
    Task RunAsync(BuildJob job, CancellationToken ct);
}
