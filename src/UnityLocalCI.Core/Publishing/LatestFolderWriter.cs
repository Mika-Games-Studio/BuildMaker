using Microsoft.Extensions.Logging;

namespace UnityLocalCI.Core.Publishing;

public interface ILatestFolderWriter
{
    /// <summary>
    /// Substitui o conteudo de latest\ pela build em <paramref name="sourceFolder"/>.
    /// Retorna a mensagem de erro, ou nulo em caso de sucesso.
    /// </summary>
    Task<string?> UpdateAsync(string artifactFolder, string sourceFolder, CancellationToken ct);
}

/// <summary>
/// A pasta latest\ e a build mais recente descompactada e pronta para rodar:
/// quem so quer testar entra, roda o rodar.bat e joga, sem baixar nem
/// descompactar nada.
///
/// A troca e feita por rename de diretorio, nunca apagando e recopiando no
/// lugar. Copiar por cima deixaria a pasta em estado parcial por varios
/// segundos, e quem a abrisse nesse intervalo pegaria uma build quebrada, sem
/// nenhum sinal de que ela estava pela metade.
/// </summary>
public sealed class LatestFolderWriter : ILatestFolderWriter
{
    public const string LatestName = "latest";
    private const string StagingName = "latest.new";
    private const string PreviousName = "latest.old";

    private readonly ILogger<LatestFolderWriter> _logger;

    public LatestFolderWriter(ILogger<LatestFolderWriter> logger) => _logger = logger;

    public async Task<string?> UpdateAsync(string artifactFolder, string sourceFolder, CancellationToken ct)
    {
        if (!Directory.Exists(sourceFolder))
            return $"a pasta da build {sourceFolder} nao existe";

        var latest = Path.Combine(artifactFolder, LatestName);
        var staging = Path.Combine(artifactFolder, StagingName);
        var previous = Path.Combine(artifactFolder, PreviousName);

        try
        {
            RecoverFromInterruptedSwap(latest, previous);

            DeleteIfExists(staging);
            DeleteIfExists(previous);

            // A copia inteira acontece num nome que ninguem procura. So depois de
            // pronta ela assume o nome latest\.
            await CopyDirectoryAsync(sourceFolder, staging, ct).ConfigureAwait(false);

            if (Directory.Exists(latest)) Directory.Move(latest, previous);
            Directory.Move(staging, latest);

            DeleteIfExists(previous);

            _logger.LogInformation("Pasta {Latest} atualizada.", latest);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Melhor deixar a latest\ anterior no lugar do que meia nova.
            DeleteIfExists(staging);
            RecoverFromInterruptedSwap(latest, previous);

            return $"nao foi possivel atualizar {latest}: {exception.Message}";
        }
    }

    /// <summary>
    /// Se o processo morreu entre os dois renames, latest\ nao existe e
    /// latest.old\ sim. Devolve a anterior ao lugar em vez de deixar o time sem
    /// pasta nenhuma.
    /// </summary>
    private void RecoverFromInterruptedSwap(string latest, string previous)
    {
        if (Directory.Exists(latest) || !Directory.Exists(previous)) return;

        Directory.Move(previous, latest);
        _logger.LogWarning(
            "A pasta {Latest} estava ausente e {Previous} existia; a troca anterior foi interrompida e a versao antiga voltou ao lugar.",
            latest, previous);
    }

    private static void DeleteIfExists(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();

            var target = Path.Combine(destination, Path.GetFileName(file));

            await using var input = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
            await using var output = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);

            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }

        foreach (var folder in Directory.EnumerateDirectories(source))
        {
            await CopyDirectoryAsync(folder, Path.Combine(destination, Path.GetFileName(folder)), ct)
                .ConfigureAwait(false);
        }
    }
}
