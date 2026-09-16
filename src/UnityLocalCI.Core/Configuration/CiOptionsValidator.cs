using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Secrets;

namespace UnityLocalCI.Core.Configuration;

/// <summary>
/// Validacao na inicializacao. Configuracao incompleta ou credencial ausente
/// derruba o processo no start com mensagem acionavel, nunca no meio da primeira build.
/// </summary>
public sealed class CiOptionsValidator : IValidateOptions<CiOptions>
{
    private readonly ICredentialStore _credentials;

    public CiOptionsValidator(ICredentialStore credentials) => _credentials = credentials;

    public ValidateOptionsResult Validate(string? name, CiOptions options)
    {
        var errors = new List<string>();

        if (options.Scheduler.MaxConcurrentBuilds < 1)
            errors.Add("Scheduler.MaxConcurrentBuilds precisa ser no minimo 1.");
        if (options.Scheduler.MinFreeRamGb < 0)
            errors.Add("Scheduler.MinFreeRamGb nao pode ser negativo.");

        if (options.Projects.Count == 0)
            errors.Add("Nenhum projeto configurado. Acrescente ao menos um bloco em 'Projects'.");

        var enabled = options.Projects.Where(p => p.Enabled).ToList();
        if (options.Projects.Count > 0 && enabled.Count == 0)
            errors.Add("Todos os projetos estao com 'Enabled: false'. Nenhuma build seria disparada.");

        var duplicates = enabled.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                                .Where(g => g.Count() > 1)
                                .Select(g => g.Key);
        foreach (var dup in duplicates)
            errors.Add($"Projeto '{dup}' aparece mais de uma vez. O nome identifica a fila e o estado, entao precisa ser unico.");

        var workspaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in enabled)
        {
            var label = string.IsNullOrWhiteSpace(project.Name) ? "<sem nome>" : project.Name;

            if (string.IsNullOrWhiteSpace(project.Name))
                errors.Add("Ha um projeto sem 'Name'. O nome identifica a fila, o estado e a pasta de destino.");

            var resolved = ProjectResolver.Resolve(project, options.Defaults);

            if (string.IsNullOrWhiteSpace(resolved.Repository.Url))
                errors.Add($"[{label}] Repository.Url nao configurado.");
            if (string.IsNullOrWhiteSpace(resolved.Repository.Branch))
                errors.Add($"[{label}] Repository.Branch nao configurado.");
            if (string.IsNullOrWhiteSpace(resolved.Repository.WorkspacePath))
                errors.Add($"[{label}] Repository.WorkspacePath nao configurado.");
            else if (!Path.IsPathRooted(resolved.Repository.WorkspacePath))
                errors.Add($"[{label}] Repository.WorkspacePath precisa ser um caminho absoluto: '{resolved.Repository.WorkspacePath}'.");
            else if (workspaces.TryGetValue(resolved.Repository.WorkspacePath, out var owner))
                errors.Add($"[{label}] compartilha o workspace '{resolved.Repository.WorkspacePath}' com '{owner}'. " +
                           "Dois projetos no mesmo diretorio corromperiam a Library do Unity; cada um precisa do seu.");
            else
                workspaces[resolved.Repository.WorkspacePath] = label;

            if (string.IsNullOrWhiteSpace(resolved.Unity.EditorVersion))
                errors.Add($"[{label}] Unity.EditorVersion nao configurado, nem no projeto nem em Defaults.");
            if (string.IsNullOrWhiteSpace(resolved.Unity.ExecuteMethod))
                errors.Add($"[{label}] Unity.ExecuteMethod nao configurado.");
            if (resolved.Unity.TimeoutMinutes <= 0)
                errors.Add($"[{label}] Unity.TimeoutMinutes precisa ser maior que zero.");

            if (string.IsNullOrWhiteSpace(resolved.Publishing.StagingFolder))
                errors.Add($"[{label}] Publishing.StagingFolder nao configurado. O staging e disco local e precede a copia para o destino.");
            if (string.IsNullOrWhiteSpace(resolved.Publishing.ArtifactFolder))
                errors.Add($"[{label}] Publishing.ArtifactFolder nao configurado.");

            if (resolved.Retention.KeepLastBuilds < 1)
                errors.Add($"[{label}] Retention.KeepLastBuilds precisa ser no minimo 1.");

            if (!string.IsNullOrWhiteSpace(project.Repository.PatCredentialName)
                && !_credentials.Exists(project.Repository.PatCredentialName!))
            {
                errors.Add($"[{label}] a credencial '{project.Repository.PatCredentialName}' nao existe no Windows Credential Manager. " +
                           $"Grave com: cmdkey /generic:{project.Repository.PatCredentialName} /user:pat /pass:<PAT>");
            }
        }

        if (string.IsNullOrWhiteSpace(options.State.DatabasePath))
            errors.Add("State.DatabasePath nao configurado.");
        if (string.IsNullOrWhiteSpace(options.State.LogFolder))
            errors.Add("State.LogFolder nao configurado.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
