using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Git;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Etapa 1: fetch, reset --hard no sha e clean preservando o cache do Unity.
/// O workspace e persistente de proposito: build limpa e cerca de 3x mais lenta.
/// </summary>
public sealed class SyncStep : IBuildStep
{
    private readonly IGitClient _git;
    private readonly ILogger<SyncStep> _logger;

    public SyncStep(IGitClient git, ILogger<SyncStep> logger)
    {
        _git = git;
        _logger = logger;
    }

    public string Name => "Sync";

    public async Task<StepResult> ExecuteAsync(BuildContext context, CancellationToken ct)
    {
        try
        {
            var outcome = await _git.EnsureWorkspaceAsync(context.Git, ct).ConfigureAwait(false);
            context.WorkspaceWasCloned = outcome.Cloned;

            if (outcome.Cloned)
            {
                context.Warnings.Add(
                    "Workspace recem-clonado: esta build sera lenta porque ainda nao ha cache da Library.");
            }

            await _git.FetchAsync(context.Git, ct).ConfigureAwait(false);
            await _git.CheckoutAsync(context.Git, context.Commit.Sha, ct).ConfigureAwait(false);

            _logger.LogInformation("Workspace sincronizado em {Sha}.", context.Commit.ShortSha);
            return StepResult.Ok;
        }
        catch (GitCommandException ex)
        {
            return StepResult.Fail($"Falha ao sincronizar o workspace: {ex.Message}{Environment.NewLine}{ex.StandardError}");
        }
    }
}
