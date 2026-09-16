using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;

namespace UnityLocalCI.Core.Queue;

public enum BuildTrigger
{
    /// <summary>Commit novo detectado pelo polling.</summary>
    Poll,
    /// <summary>Arquivo de gatilho tocado pelo usuario (fase 2).</summary>
    Manual,
    /// <summary>Build interrompida por reinicio da maquina e recolocada na fila no boot.</summary>
    Recovery,
}

/// <summary>Um job enfileirado. O BuildId ja existe no banco antes de entrar na fila.</summary>
public sealed record BuildJob
{
    public required long BuildId { get; init; }
    public required ResolvedProject Project { get; init; }
    public required CommitInfo Commit { get; init; }
    public required BuildTrigger Trigger { get; init; }
    public required DateTimeOffset QueuedAt { get; init; }
}
