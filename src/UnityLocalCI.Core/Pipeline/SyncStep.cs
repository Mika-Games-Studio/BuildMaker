using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Unity;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Etapa 1: fetch, reset --hard no sha, clean preservando o cache do Unity, e o
/// Builder.cs escrito dentro do projeto.
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

            // Depois do checkout, nunca antes: o clean nao remove o script (ele
            // esta nas exclusoes), mas um clone novo chega sem ele.
            if (BuilderScript.Deploy(context.Git.WorkspacePath))
                _logger.LogInformation("Builder.cs escrito em {Caminho}.", BuilderScript.RelativePath);

            return StepResult.Ok;
        }
        catch (GitCommandException ex)
        {
            return StepResult.Fail($"Falha ao sincronizar o workspace: {ex.Message}{Environment.NewLine}{ex.StandardError}");
        }
        catch (IOException ex)
        {
            return StepResult.Fail($"Falha ao escrever o {BuilderScript.RelativePath} no projeto: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return StepResult.Fail($"Sem permissao para escrever o {BuilderScript.RelativePath} no projeto: {ex.Message}");
        }
    }
}
