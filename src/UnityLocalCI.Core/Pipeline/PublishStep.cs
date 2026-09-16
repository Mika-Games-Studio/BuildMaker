using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Etapa 4: copia o artefato para o destino.
///
/// E a unica etapa cuja falha nao derruba a build. O zip existe em disco local e
/// nao e descartado; a copia fica pendente e sera retentada.
/// </summary>
public sealed class PublishStep : IBuildStep
{
    private readonly IArtifactPublisher _publisher;
    private readonly ILogger<PublishStep> _logger;

    public PublishStep(IArtifactPublisher publisher, ILogger<PublishStep> logger)
    {
        _publisher = publisher;
        _logger = logger;
    }

    public string Name => "Publish";

    /// <summary>Resultado da ultima execucao, lido pelo pipeline para gravar o publish_status.</summary>
    public PublishStatus LastStatus { get; private set; } = PublishStatus.None;

    public async Task<StepResult> ExecuteAsync(BuildContext context, CancellationToken ct)
    {
        if (context.ArtifactPath is null || !File.Exists(context.ArtifactPath))
        {
            LastStatus = PublishStatus.Failed;
            return StepResult.Fail("Nao ha artefato para publicar.");
        }

        var result = await _publisher
            .PublishAsync(new FileInfo(context.ArtifactPath), context, ct)
            .ConfigureAwait(false);

        if (result.Success)
        {
            context.PublishedPath = result.Location;
            LastStatus = PublishStatus.Published;
            return StepResult.Ok;
        }

        LastStatus = PublishStatus.PendingCopy;
        context.Warnings.Add(
            $"Artefato nao foi copiado para o destino ({result.Error}). " +
            $"O zip esta em {context.ArtifactPath} e a copia sera retentada.");

        _logger.LogWarning(
            "Publicacao pendente: {Error}. O artefato permanece em {Staging}.",
            result.Error, context.ArtifactPath);

        // A build continua bem-sucedida de proposito.
        return StepResult.Ok;
    }
}
