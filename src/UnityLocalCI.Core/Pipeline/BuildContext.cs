using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Queue;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>Estado que atravessa as etapas de uma build.</summary>
public sealed class BuildContext
{
    public required long BuildId { get; init; }
    public required ResolvedProject Project { get; init; }
    public required CommitInfo Commit { get; init; }
    public required BuildTrigger Trigger { get; init; }
    public required GitContext Git { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Log da build, escrito em tempo real.</summary>
    public required string LogPath { get; init; }

    /// <summary>Pasta onde o Unity grava o player. Sempre disco local.</summary>
    public required string BuildOutputPath { get; init; }

    /// <summary>Zip gerado. Vive no staging local ate a copia ser confirmada.</summary>
    public string? ArtifactPath { get; set; }

    public string? ArtifactSha256 { get; set; }
    public long? ArtifactSizeBytes { get; set; }
    public string? PublishedPath { get; set; }

    /// <summary>Avisos destacados, como a combinacao Brotli sem fallback.</summary>
    public List<string> Warnings { get; } = new();

    public bool WorkspaceWasCloned { get; set; }
}
