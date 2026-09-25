using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Pipeline;

namespace UnityLocalCI.Core.Publishing;

/// <summary>
/// Publica copiando o zip para a pasta de destino.
///
/// E tudo o que ele faz, e a pasta de destino contem exatamente isso: os zips
/// das builds. Havia tambem uma pasta latest\ com a ultima build ja
/// descompactada, trocada por rename de diretorio para nunca ficar em estado
/// parcial. Ela saiu: quem quer a build pega o zip, e a pasta que o time abre
/// fica com uma coisa so dentro, do tipo que se espera encontrar ali.
///
/// Resiliencia: o zip ja existe no staging local quando esta etapa comeca. Se o
/// compartilhamento estiver indisponivel ou sem permissao, a build permanece
/// bem-sucedida, a copia fica marcada como pendente e o staging nao e apagado.
/// </summary>
public sealed class FolderPublisher : IArtifactPublisher
{
    private readonly ArtifactCopier _copier;
    private readonly ILogger<FolderPublisher> _logger;

    public FolderPublisher(ArtifactCopier copier, ILogger<FolderPublisher> logger)
    {
        _copier = copier;
        _logger = logger;
    }

    public string Name => "Pasta";

    public async Task<PublishResult> PublishAsync(FileInfo artifact, BuildContext context, CancellationToken ct)
    {
        var outcome = await _copier
            .CopyAsync(
                artifact.FullName,
                context.Project.Publishing.ArtifactFolder,
                context.ArtifactSha256,
                ct)
            .ConfigureAwait(false);

        if (!outcome.Success) return PublishResult.Pending(outcome.Error!);

        _logger.LogInformation("Artefato publicado em {Path}.", outcome.Location);
        return PublishResult.Published(outcome.Location!);
    }
}
