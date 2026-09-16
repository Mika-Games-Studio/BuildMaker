using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Notifications;

/// <summary>
/// Escreve _STATUS.txt e _HISTORICO.txt na pasta de destino do projeto.
///
/// Sempre ativo, inclusive quando a build falha: o criterio de aceite 3 pede
/// justamente que o erro de compilacao apareca no _STATUS.txt, e uma build que
/// falha em silencio e o cenario que este arquivo existe para evitar.
/// </summary>
public sealed class StatusFileNotifier : INotifier
{
    private const string StatusFileName = "_STATUS.txt";
    private const string HistoryFileName = "_HISTORICO.txt";
    private const string LogFolderName = "_logs";

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
                "Projeto {Project} nao esta na configuracao; arquivos de status nao serao escritos.", build.Project);
            return;
        }

        if (!project.Publishing.WriteStatusFiles) return;

        var folder = project.Publishing.ArtifactFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            // O log primeiro: o _STATUS.txt aponta para ele, e mandar alguem abrir
            // um arquivo que ainda nao foi copiado e pior que nao dizer nada.
            await CopyLogAsync(folder, build, ct).ConfigureAwait(false);
            await WriteStatusAsync(folder, build, warnings, project, ct).ConfigureAwait(false);
            await WriteHistoryAsync(folder, build.Project, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Mesma regra do artefato: destino indisponivel nao derruba a build.
            _logger.LogWarning(
                "Nao foi possivel escrever os arquivos de status em {Folder}: {Message}", folder, exception.Message);
        }
    }

    /// <summary>
    /// Copia o log da build para _logs\ na pasta de destino. Quem abre o
    /// _STATUS.txt e ve uma falha precisa do log ao lado, nao numa maquina que
    /// ele nao acessa.
    /// </summary>
    private async Task CopyLogAsync(string folder, BuildRecord build, CancellationToken ct)
    {
        if (build.LogPath is not { } source || !File.Exists(source)) return;

        var destinationFolder = Path.Combine(folder, LogFolderName);
        Directory.CreateDirectory(destinationFolder);

        var destination = Path.Combine(destinationFolder, $"build-{build.Id}.log");

        // FileShare.ReadWrite na origem: o BuildLogWriter pode nao ter fechado o
        // arquivo ainda quando o notificador roda.
        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 64, useAsync: true);
        await using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true);

        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    private async Task WriteStatusAsync(
        string folder,
        BuildRecord build,
        IReadOnlyList<string> warnings,
        ResolvedProject project,
        CancellationToken ct)
    {
        var previous = await FindPreviousAsync(build, ct).ConfigureAwait(false);

        var content = StatusFormatter.FormatProjectStatus(
            build, previous, warnings, PlayHint(project, folder, build));

        await AtomicFile.WriteAllTextAsync(Path.Combine(folder, StatusFileName), content, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A dica de "como jogar" so aparece quando a pasta latest\ existe de fato.
    /// Mandar alguem abrir um arquivo que nao esta la e pior que nao dizer nada.
    /// </summary>
    private static string? PlayHint(ResolvedProject project, string folder, BuildRecord build)
    {
        if (build.Status != BuildStatus.Succeeded) return null;
        if (!project.Publishing.MaintainLatestFolder) return null;

        var launcher = Path.Combine(folder, "latest", "rodar.bat");
        return File.Exists(launcher) ? @"abra latest\rodar.bat" : null;
    }

    private async Task WriteHistoryAsync(string folder, string project, CancellationToken ct)
    {
        // Reconstruido do banco, e nao acrescentado ao arquivo: o banco e a fonte
        // da verdade, e assim o historico se corrige sozinho se o arquivo sumir.
        var recent = await _store
            .GetRecentAsync(project, StatusFormatter.HistoryLength * 3, ct)
            .ConfigureAwait(false);

        var relevant = recent
            .Where(Ran)
            .Take(StatusFormatter.HistoryLength)
            .Reverse()
            .ToList();

        var content = StatusFormatter.FormatHistory(relevant);
        await AtomicFile.WriteAllTextAsync(Path.Combine(folder, HistoryFileName), content, ct).ConfigureAwait(false);
    }

    private async Task<BuildRecord?> FindPreviousAsync(BuildRecord build, CancellationToken ct)
    {
        var recent = await _store.GetRecentAsync(build.Project, 10, ct).ConfigureAwait(false);
        return recent.FirstOrDefault(b => b.Id != build.Id && Ran(b));
    }

    /// <summary>
    /// Builds canceladas pelo debounce nunca chegaram a rodar. Mostra-las no
    /// historico so acrescentaria ruido a um arquivo que existe para ser lido
    /// de relance.
    /// </summary>
    private static bool Ran(BuildRecord build)
        => build.Status is BuildStatus.Succeeded or BuildStatus.Failed or BuildStatus.Interrupted;

    private ResolvedProject? FindProject(string name)
        => ProjectResolver
            .ResolveEnabled(_options.CurrentValue)
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
