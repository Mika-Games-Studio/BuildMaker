using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Publishing;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Etapa 3: valida a saida, gera o manifest, compacta e calcula o SHA-256.
/// O zip nasce no staging local; a copia para o destino e problema da etapa 4.
/// </summary>
public sealed class PackageStep : IBuildStep
{
    private readonly IClock _clock;
    private readonly ILogger<PackageStep> _logger;

    public PackageStep(IClock clock, ILogger<PackageStep> logger)
    {
        _clock = clock;
        _logger = logger;
    }

    public string Name => "Package";

    public async Task<StepResult> ExecuteAsync(BuildContext context, CancellationToken ct)
    {
        var validation = ValidateOutput(context.BuildOutputPath);
        if (validation is not null) return StepResult.Fail(validation);

        await WriteManifestAsync(context, ct).ConfigureAwait(false);

        // O launcher entra antes de zipar, entao a mesma escrita serve ao zip e a
        // pasta latest\, que e copiada desta mesma saida.
        if (context.Project.Packaging.IncludeLauncher)
        {
            await LauncherScript.WriteAsync(context.BuildOutputPath, ct).ConfigureAwait(false);
            _logger.LogInformation("Launcher {Launcher} incluido na build.", LauncherScript.LauncherFileName);
        }

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

    private async Task WriteManifestAsync(BuildContext context, CancellationToken ct)
    {
        var manifest = new
        {
            buildId = context.BuildId,
            project = context.Project.Name,
            branch = context.Project.Repository.Branch,
            commitSha = context.Commit.Sha,
            commitAuthor = context.Commit.Author,
            commitMessage = context.Commit.Message,
            editorVersion = context.Project.Unity.EditorVersion,
            buildTarget = context.Project.Unity.BuildTarget,
            startedAt = context.StartedAt,
            finishedAt = _clock.UtcNow,
            durationSeconds = (int)(_clock.UtcNow - context.StartedAt).TotalSeconds,
            warnings = context.Warnings,
        };

        var path = Path.Combine(context.BuildOutputPath, "manifest.json");
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

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
