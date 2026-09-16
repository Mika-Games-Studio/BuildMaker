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
    private const string PartialSuffix = ".part";

    private readonly ILogger<FolderPublisher> _logger;

    public FolderPublisher(ILogger<FolderPublisher> logger) => _logger = logger;

    public string Name => "Pasta";

    public async Task<PublishResult> PublishAsync(FileInfo artifact, BuildContext context, CancellationToken ct)
    {
        var destinationFolder = context.Project.Publishing.ArtifactFolder;
        var finalPath = Path.Combine(destinationFolder, artifact.Name);
        var partialPath = finalPath + PartialSuffix;

        try
        {
            Directory.CreateDirectory(destinationFolder);
        }
        catch (UnauthorizedAccessException)
        {
            return PublishResult.Pending($"sem permissao de escrita em {destinationFolder}");
        }
        catch (IOException ex)
        {
            return PublishResult.Pending($"pasta de destino {destinationFolder} indisponivel: {ex.Message}");
        }

        try
        {
            // Nome temporario ate o fim da copia: ninguem que estiver olhando a
            // pasta pode pegar um zip pela metade com o nome final.
            if (File.Exists(partialPath)) File.Delete(partialPath);

            await CopyAsync(artifact.FullName, partialPath, ct).ConfigureAwait(false);

            var copiedHash = await PackageStep.ComputeSha256Async(partialPath, ct).ConfigureAwait(false);
            if (!string.Equals(copiedHash, context.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
            {
                SafeDelete(partialPath);
                return PublishResult.Pending(
                    "o SHA-256 do arquivo copiado nao confere com o do staging; a copia foi descartada");
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partialPath, finalPath);

            _logger.LogInformation("Artefato publicado em {Path}.", finalPath);
            return PublishResult.Published(finalPath);
        }
        catch (UnauthorizedAccessException)
        {
            SafeDelete(partialPath);
            return PublishResult.Pending($"sem permissao de escrita em {destinationFolder}");
        }
        catch (IOException ex)
        {
            SafeDelete(partialPath);
            return PublishResult.Pending($"falha ao copiar para {destinationFolder}: {ex.Message}");
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        await using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);

        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    private void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel remover a copia parcial {Path}.", path);
        }
        catch (UnauthorizedAccessException) { /* destino ja inacessivel */ }
    }
}
