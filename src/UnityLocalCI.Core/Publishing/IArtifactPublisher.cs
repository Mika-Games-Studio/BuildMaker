using UnityLocalCI.Core.Pipeline;

namespace UnityLocalCI.Core.Publishing;

public sealed record PublishResult(bool Success, string? Location, string? Error)
{
    public static PublishResult Published(string location) => new(true, location, null);
    public static PublishResult Pending(string error) => new(false, null, error);
}

/// <summary>
/// A interface existe mesmo havendo uma implementacao so. E barata e mantem
/// aberta a porta para um destino remoto no futuro, sem tocar no pipeline.
/// </summary>
public interface IArtifactPublisher
{
    string Name { get; }
    Task<PublishResult> PublishAsync(FileInfo artifact, BuildContext context, CancellationToken ct);
}
