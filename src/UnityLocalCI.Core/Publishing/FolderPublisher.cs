using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Pipeline;

namespace UnityLocalCI.Core.Publishing;

/// <summary>
/// Publica copiando o zip para a pasta de destino.
///
/// Resiliencia: o zip ja existe no staging local quando esta etapa comeca. Se o
/// compartilhamento estiver indisponivel ou sem permissao, a build permanece
/// bem-sucedida, a copia fica marcada como pendente e o staging nao e apagado.
/// </summary>
public sealed class FolderPublisher : IArtifactPublisher
{
    private readonly ArtifactCopier _copier;
    private readonly ILatestFolderWriter _latest;
    private readonly ILogger<FolderPublisher> _logger;

    public FolderPublisher(
        ArtifactCopier copier, ILatestFolderWriter latest, ILogger<FolderPublisher> logger)
    {
        _copier = copier;
        _latest = latest;
        _logger = logger;
    }

    public string Name => "Pasta";

    public async Task<PublishResult> PublishAsync(FileInfo artifact, BuildContext context, CancellationToken ct)
    {
        var destinationFolder = context.Project.Publishing.ArtifactFolder;

        var outcome = await _copier
            .CopyAsync(artifact.FullName, destinationFolder, context.ArtifactSha256, ct)
            .ConfigureAwait(false);

        if (!outcome.Success) return PublishResult.Pending(outcome.Error!);

        _logger.LogInformation("Artefato publicado em {Path}.", outcome.Location);

        await UpdateLatestAsync(destinationFolder, context, ct).ConfigureAwait(false);

        return PublishResult.Published(outcome.Location!);
    }

    /// <summary>
    /// A latest\ vem depois do zip e nao pode derrubar a publicacao: o artefato
    /// ja esta no destino e e ele que importa. Uma latest\ desatualizada faz
    /// alguem testar a build errada, entao a falha vira aviso no _STATUS.txt.
    /// </summary>
    private async Task UpdateLatestAsync(string destinationFolder, BuildContext context, CancellationToken ct)
    {
        if (!context.Project.Publishing.MaintainLatestFolder) return;

        var error = await _latest
            .UpdateAsync(destinationFolder, context.BuildOutputPath, ct)
            .ConfigureAwait(false);

        if (error is null) return;

        context.Warnings.Add(
            $"A pasta latest\\ nao foi atualizada ({error}); ela ainda contem a build anterior.");

        _logger.LogWarning("Falha ao atualizar a pasta latest: {Error}", error);
    }
}
