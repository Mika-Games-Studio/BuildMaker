using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Unity;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Etapa 2: invoca o Unity.
///
/// Sucesso e determinado pelo exit code, nunca pela existencia da pasta de
/// saida: o Unity deixa artefatos parciais em disco mesmo quando falha.
/// </summary>
public sealed class UnityBuildStep : IBuildStep
{
    private readonly IUnityCliClient _unity;
    private readonly IBuildLogWriter _log;
    private readonly ILogger<UnityBuildStep> _logger;

    public UnityBuildStep(IUnityCliClient unity, IBuildLogWriter log, ILogger<UnityBuildStep> logger)
    {
        _unity = unity;
        _log = log;
        _logger = logger;
    }

    public string Name => "Build";

    public async Task<StepResult> ExecuteAsync(BuildContext context, CancellationToken ct)
    {
        // Saida sempre limpa: restos de uma build anterior confundiriam o
        // empacotamento, ja que nao usamos a pasta para decidir sucesso.
        if (Directory.Exists(context.BuildOutputPath))
            Directory.Delete(context.BuildOutputPath, recursive: true);
        Directory.CreateDirectory(context.BuildOutputPath);

        var request = new UnityBuildRequest
        {
            ProjectPath = context.Project.Repository.WorkspacePath,
            EditorVersion = context.Project.Unity.EditorVersion,
            BuildTarget = context.Project.Unity.BuildTarget,
            ExecuteMethod = context.Project.Unity.ExecuteMethod,
            OutputPath = context.BuildOutputPath,
            LogFilePath = context.LogPath,
            Timeout = TimeSpan.FromMinutes(context.Project.Unity.TimeoutMinutes),
            ExtraArgs = context.Project.Unity.ExtraArgs,
            CustomArguments = new Dictionary<string, string>
            {
                ["-ciBuildNumber"] = context.BuildId.ToString(),
                ["-ciCommitSha"] = context.Commit.Sha,
                ["-ciBranch"] = context.Project.Repository.Branch,
                ["-ciOutputPath"] = context.BuildOutputPath,
            },
        };

        var result = await _unity
            .BuildAsync(request, line => _log.Write(line), ct)
            .ConfigureAwait(false);

        foreach (var warning in ExtractHighlightedWarnings(result))
            context.Warnings.Add(warning);

        if (result.Succeeded)
        {
            _logger.LogInformation("Unity terminou com exit code 0.");
            return StepResult.Ok;
        }

        var summary = result.ErrorSummary ?? $"Unity terminou com exit code {result.ExitCode}.";
        _logger.LogError("Build falhou: {Summary}", summary);
        return StepResult.Fail(summary);
    }

    /// <summary>
    /// O Builder.cs emite o aviso de compressao WebGL no log com um prefixo
    /// conhecido. O pipeline apenas o propaga: nao corrige nem aborta, porque
    /// Player Settings sao responsabilidade de quem mantem o projeto Unity.
    /// </summary>
    private static IEnumerable<string> ExtractHighlightedWarnings(UnityBuildResult result)
        => result.Warnings
            .Where(w => w.Contains("[UnityLocalCI]", StringComparison.Ordinal))
            .Distinct();
}
