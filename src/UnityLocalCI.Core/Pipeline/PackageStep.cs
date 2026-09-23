using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Etapa 3: valida a saida, compacta e calcula o SHA-256. O zip nasce no
/// staging local; a copia para o destino e problema da etapa 4.
///
/// Nada e acrescentado a saida do Unity. Esta etapa ja escreveu ali um
/// manifest.json e um rodar.bat que subia um servidor estatico — a build WebGL
/// nao abre por file://, e o launcher resolvia isso para quem so queria testar.
/// Os dois sairam: o zip e a pasta latest\ sao o jogo, e arquivo de CI no meio
/// dos arquivos do jogo confunde quem recebe e derruba a validacao de um portal.
///
/// O que o manifest dizia nao se perdeu — commit, autor, duracao e tamanho
/// estao no status em JSON, em %LOCALAPPDATA%\BuildMaker\status. O que se perdeu
/// e poder abrir a latest\ com um duplo clique; agora ela precisa de um servidor.
/// </summary>
public sealed class PackageStep : IBuildStep
{
    private readonly ILogger<PackageStep> _logger;

    public PackageStep(ILogger<PackageStep> logger) => _logger = logger;

    public string Name => "Package";

    public async Task<StepResult> ExecuteAsync(BuildContext context, CancellationToken ct)
    {
        var validation = ValidateOutput(context.BuildOutputPath);
        if (validation is not null) return StepResult.Fail(validation);

        var staging = context.Project.Publishing.StagingFolder;
        Directory.CreateDirectory(staging);

        var zipPath = Path.Combine(staging, BuildArtifactName(context));
        if (File.Exists(zipPath)) File.Delete(zipPath);

        ZipFile.CreateFromDirectory(
            context.BuildOutputPath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

        var info = new FileInfo(zipPath);
        context.ArtifactPath = zipPath;
        context.ArtifactSizeBytes = info.Length;
        context.ArtifactSha256 = await ComputeSha256Async(zipPath, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Artefato gerado em {Path} ({Size}).", zipPath, FormatSize(info.Length));

        return StepResult.Ok;
    }

    /// <summary>
    /// A validacao existe para detectar saida vazia, nao para decidir sucesso:
    /// isso ja foi decidido pelo exit code na etapa anterior.
    /// </summary>
    private static string? ValidateOutput(string outputPath)
    {
        if (!Directory.Exists(outputPath))
            return $"A pasta de saida {outputPath} nao existe apos a build.";

        if (!File.Exists(Path.Combine(outputPath, "index.html")))
            return $"A pasta de saida {outputPath} nao contem index.html.";

        if (!Directory.Exists(Path.Combine(outputPath, "Build")))
            return $"A pasta de saida {outputPath} nao contem a subpasta Build/.";

        return null;
    }

    public static string BuildArtifactName(BuildContext context)
    {
        var name = context.Project.Packaging.NamePattern
            .Replace("{project}", Sanitize(context.Project.Name), StringComparison.Ordinal)
            .Replace("{branch}", Sanitize(context.Project.Repository.Branch), StringComparison.Ordinal)
            .Replace("{date}", context.StartedAt.ToLocalTime().ToString("yyyyMMdd"), StringComparison.Ordinal)
            .Replace("{sha}", context.Commit.ShortSha, StringComparison.Ordinal)
            .Replace("{build}", context.BuildId.ToString(), StringComparison.Ordinal);

        return name;
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 128, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string FormatSize(long bytes)
    {
        const double megabyte = 1024d * 1024d;
        return bytes >= megabyte
            ? $"{bytes / megabyte:0.0} MB"
            : $"{bytes / 1024d:0.0} KB";
    }
}
