using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Hosting;

/// <summary>
/// Roda antes de scheduler e watchers: cria os diretorios, inicializa o banco e
/// avalia as builds orfas do boot.
/// </summary>
public sealed class StartupService : IHostedService
{
    private readonly IBuildStore _store;
    private readonly OrphanRecovery _recovery;
    private readonly IGlobalStatusWriter _globalStatus;
    private readonly IOptions<CiOptions> _options;
    private readonly ILogger<StartupService> _logger;

    public StartupService(
        IBuildStore store,
        OrphanRecovery recovery,
        IGlobalStatusWriter globalStatus,
        IOptions<CiOptions> options,
        ILogger<StartupService> logger)
    {
        _store = store;
        _recovery = recovery;
        _globalStatus = globalStatus;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configuration = _options.Value;
        var projects = ProjectResolver.ResolveEnabled(configuration);

        CreateLocalFolders(configuration, projects);

        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _recovery.RecoverAsync(projects, cancellationToken).ConfigureAwait(false);

        // O geral.json e reescrito logo depois da recuperacao.
        //
        // Ele so era escrito de dentro do pipeline, quando uma build comeca ou
        // termina. Se o processo morre no meio de uma — e aqui isso acontece,
        // porque a heuristica de ransomware do antivirus corporativo mata o
        // programa durante o empacotamento —, o arquivo fica congelado dizendo
        // "1 em execucao" para uma build que nao existe mais. A recuperacao logo
        // acima ja corrigiu isso no banco; sem esta linha, o arquivo que as
        // pessoas abrem continuava mentindo ate a proxima build terminar.
        await _globalStatus.WriteAsync(cancellationToken).ConfigureAwait(false);

        foreach (var project in projects)
        {
            if (WorkspacePathLimit.Arriscado(project.Repository.WorkspacePath))
            {
                _logger.LogWarning("{Aviso}",
                    WorkspacePathLimit.Explicacao(project.Name, project.Repository.WorkspacePath));
            }
        }

        _logger.LogInformation(
            "UnityLocalCI iniciado com {Count} projeto(s): {Projects}.",
            projects.Count, string.Join(", ", projects.Select(p => p.Name)));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("UnityLocalCI encerrando.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// So diretorios locais. O destino pode ser um compartilhamento de rede que
    /// ainda nao esta disponivel, e isso nao pode impedir o servico de subir.
    /// </summary>
    private void CreateLocalFolders(CiOptions configuration, IReadOnlyList<ResolvedProject> projects)
    {
        foreach (var project in projects)
        {
            Directory.CreateDirectory(project.Publishing.StagingFolder);

            var workspaceParent = Path.GetDirectoryName(
                project.Repository.WorkspacePath.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(workspaceParent)) Directory.CreateDirectory(workspaceParent);

            if (!string.IsNullOrWhiteSpace(project.ManualTriggerFile))
            {
                var triggerFolder = Path.GetDirectoryName(project.ManualTriggerFile);
                if (!string.IsNullOrEmpty(triggerFolder)) Directory.CreateDirectory(triggerFolder);
            }
        }
    }
}
