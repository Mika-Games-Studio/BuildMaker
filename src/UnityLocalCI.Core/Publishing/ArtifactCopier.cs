using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Pipeline;

namespace UnityLocalCI.Core.Publishing;

public sealed record CopyOutcome(bool Success, string? Location, string? Error)
{
    public static CopyOutcome Ok(string location) => new(true, location, null);
    public static CopyOutcome Fail(string error) => new(false, null, error);
}

/// <summary>
/// A copia do zip para a pasta de destino, com nome temporario e conferencia de
/// hash. Vive separada do FolderPublisher porque o reenvio de copia pendente
/// precisa exatamente das mesmas garantias, e duplicar isso seria duplicar as
/// regras que protegem o artefato.
/// </summary>
public sealed class ArtifactCopier
{
    private const string PartialSuffix = ".part";

    private readonly ILogger<ArtifactCopier> _logger;

    public ArtifactCopier(ILogger<ArtifactCopier> logger) => _logger = logger;

    public async Task<CopyOutcome> CopyAsync(
        string sourceFile, string destinationFolder, string? expectedSha256, CancellationToken ct)
    {
        if (!File.Exists(sourceFile))
            return CopyOutcome.Fail($"o artefato {sourceFile} nao existe mais no staging");

        var finalPath = Path.Combine(destinationFolder, Path.GetFileName(sourceFile));
        var partialPath = finalPath + PartialSuffix;

        try
        {
            Directory.CreateDirectory(destinationFolder);
        }
        catch (UnauthorizedAccessException)
        {
            return CopyOutcome.Fail($"sem permissao de escrita em {destinationFolder}");
        }
        catch (IOException exception)
        {
            return CopyOutcome.Fail($"pasta de destino {destinationFolder} indisponivel: {exception.Message}");
        }

        try
        {
            // Nome temporario ate o fim da copia: ninguem que estiver olhando a
            // pasta pode pegar um zip pela metade com o nome final.
            SafeDelete(partialPath);
            await StreamAsync(sourceFile, partialPath, ct).ConfigureAwait(false);

            if (expectedSha256 is not null)
            {
                var copied = await PackageStep.ComputeSha256Async(partialPath, ct).ConfigureAwait(false);
                if (!string.Equals(copied, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    SafeDelete(partialPath);
                    return CopyOutcome.Fail(
                        "o SHA-256 do arquivo copiado nao confere com o do staging; a copia foi descartada");
                }
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partialPath, finalPath);

            return CopyOutcome.Ok(finalPath);
        }
        catch (UnauthorizedAccessException)
        {
            SafeDelete(partialPath);
            return CopyOutcome.Fail($"sem permissao de escrita em {destinationFolder}");
        }
        catch (IOException exception)
        {
            SafeDelete(partialPath);
            return CopyOutcome.Fail($"falha ao copiar para {destinationFolder}: {exception.Message}");
        }
    }

    private static async Task StreamAsync(string source, string destination, CancellationToken ct)
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
        catch (IOException exception)
        {
            _logger.LogDebug("Nao foi possivel remover a copia parcial {Path}: {Message}", path, exception.Message);
        }
        catch (UnauthorizedAccessException) { /* destino ja inacessivel */ }
    }
}
