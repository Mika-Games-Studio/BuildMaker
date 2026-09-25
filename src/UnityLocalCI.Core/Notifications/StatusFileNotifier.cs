using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Notifications;

/// <summary>
/// Escreve o status do projeto em JSON, em %LOCALAPPDATA%\BuildMaker\status.
///
/// Sempre ativo, inclusive quando a build falha: uma build que falha em silencio
/// e o cenario que este arquivo existe para evitar.
///
/// Duas coisas mudaram de lugar aqui. O arquivo saiu da pasta de destino das
/// builds — la ficam os zips, e mais nada. E o log da build deixou de ser
/// copiado junto, porque log de build nao vai mais para disco: ele existe
/// enquanto a build roda, na janela, e acaba com ela.
/// </summary>
public sealed class StatusFileNotifier : INotifier
{
    private readonly IBuildStore _store;
    private readonly IOptionsMonitor<CiOptions> _options;
    private readonly ILogger<StatusFileNotifier> _logger;

    public StatusFileNotifier(
        IBuildStore store,
        IOptionsMonitor<CiOptions> options,
        ILogger<StatusFileNotifier> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    public async Task NotifyAsync(BuildRecord build, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        var project = FindProject(build.Project);
        if (project is null)
        {
            _logger.LogWarning(
                "Projeto {Project} nao esta na configuracao; o status nao sera escrito.", build.Project);
            return;
        }

        if (!project.Publishing.WriteStatusFiles) return;

        var path = AppPaths.ProjectStatusFile(build.Project);

        try
        {
            Directory.CreateDirectory(AppPaths.StatusFolder);

            var document = new ProjectStatusDocument(
                Projeto: build.Project,
                GeradoEm: DateTimeOffset.Now,
                Atual: StatusDocuments.Describe(build),
                Anterior: await FindPreviousAsync(build, ct).ConfigureAwait(false) is { } previous
                    ? StatusDocuments.Describe(previous)
                    : null,
                Avisos: warnings,
                Historico: await BuildHistoryAsync(build.Project, ct).ConfigureAwait(false));

            await AtomicFile
                .WriteAllTextAsync(path, StatusDocuments.Serialize(document), ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Mesma regra do artefato: falha ao escrever status nao derruba a build.
            _logger.LogWarning(
                "Nao foi possivel escrever o status em {Path}: {Message}", path, exception.Message);
        }
    }

    private async Task<IReadOnlyList<BuildStatusDocument>> BuildHistoryAsync(string project, CancellationToken ct)
    {
        // Reconstruido do banco, e nao acrescentado ao arquivo: o banco e a fonte
        // da verdade, e assim o historico se corrige sozinho se o arquivo sumir.
        var recent = await _store
            .GetRecentAsync(project, StatusDocuments.HistoryLength * 3, ct)
            .ConfigureAwait(false);

        return recent
            .Where(Ran)
            .Take(StatusDocuments.HistoryLength)
            .Select(StatusDocuments.Describe)
            .ToList();
    }

    private async Task<BuildRecord?> FindPreviousAsync(BuildRecord build, CancellationToken ct)
    {
        var recent = await _store.GetRecentAsync(build.Project, 10, ct).ConfigureAwait(false);
        return recent.FirstOrDefault(b => b.Id != build.Id && Ran(b));
    }

    /// <summary>
    /// Builds canceladas pelo debounce nunca chegaram a rodar. Mostra-las no
    /// historico so acrescentaria ruido.
    /// </summary>
    private static bool Ran(BuildRecord build)
        => build.Status is BuildStatus.Succeeded or BuildStatus.Failed or BuildStatus.Interrupted;

    private ResolvedProject? FindProject(string name)
        => ProjectResolver
            .ResolveEnabled(_options.CurrentValue)
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
