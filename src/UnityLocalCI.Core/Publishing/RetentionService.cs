using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Publishing;

public interface IRetentionService
{
    /// <summary>Mantem as KeepLastBuilds builds bem-sucedidas mais recentes. Retorna quantas foram podadas.</summary>
    Task<int> ApplyAsync(ResolvedProject project, CancellationToken ct);
}

/// <summary>
/// Retencao por contagem, executada apos cada build.
///
/// A poda e guiada pelo banco, e nao por uma varredura da pasta: apagamos
/// exatamente os arquivos que cada build registrou, e nunca um zip que alguem
/// copiou para la na mao, ou a pasta latest\.
/// </summary>
public sealed class RetentionService : IRetentionService
{
    /// <summary>Quantas builds olhar para tras. Bem acima de qualquer KeepLastBuilds razoavel.</summary>
    private const int ScanDepth = 200;

    private readonly IBuildStore _store;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(IBuildStore store, ILogger<RetentionService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<int> ApplyAsync(ResolvedProject project, CancellationToken ct)
    {
        var keepCount = project.Retention.KeepLastBuilds;
        if (keepCount < 1) return 0;

        var recent = await _store.GetRecentAsync(project.Name, ScanDepth, ct).ConfigureAwait(false);

        // A contagem e de builds bem-sucedidas: uma sequencia de falhas nao pode
        // empurrar para fora o ultimo artefato que de fato funciona.
        var keep = recent
            .Where(b => b.Status == BuildStatus.Succeeded)
            .OrderByDescending(b => b.Id)
            .Take(keepCount)
            .Select(b => b.Id)
            .ToHashSet();

        // E as mais recentes de qualquer resultado. Sem isto, a build que acabou
        // de falhar seria podada na mesma execucao que a criou, e o status
        // apontaria para um artefato que a retencao ja tinha apagado.
        foreach (var id in recent.OrderByDescending(b => b.Id).Take(keepCount).Select(b => b.Id))
            keep.Add(id);

        var pruned = 0;

        foreach (var build in recent.Where(b => !keep.Contains(b.Id)))
        {
            ct.ThrowIfCancellationRequested();
            if (await PruneAsync(project, build, ct).ConfigureAwait(false)) pruned++;
        }

        if (pruned > 0)
        {
            _logger.LogInformation(
                "[{Project}] retencao removeu os arquivos de {Count} build(s), mantendo as {Keep} bem-sucedidas mais recentes.",
                project.Name, pruned, keepCount);
        }

        return pruned;
    }

    private async Task<bool> PruneAsync(ResolvedProject project, BuildRecord build, CancellationToken ct)
    {
        var removedAnything = false;
        var record = build;

        if (build.PublishedPath is { } published && Delete(published))
        {
            record = record with { PublishedPath = null };
            removedAnything = true;
        }

        // O staging so pode ser apagado depois que a copia foi confirmada. Uma
        // build com copia pendente ainda tem o artefato so ali.
        if (build.PublishStatus == PublishStatus.Published && build.ArtifactPath is { } staged && Delete(staged))
        {
            record = record with { ArtifactPath = null };
            removedAnything = true;
        }

        // Sobras de quando o log da build ia para disco. Nao ha mais o que criar
        // aqui; isto so apaga o que ficou de instalacoes anteriores, e some
        // sozinho quando nao houver mais nenhum.
        removedAnything |= Delete(Path.Combine(project.Publishing.ArtifactFolder, "_logs", $"build-{build.Id}.log"));

        if (!removedAnything) return false;

        // O registro para de apontar para arquivos que nao existem mais, senao o
        // historico em JSON continuaria oferecendo um zip que ja foi apagado.
        await _store.UpdateAsync(record, ct).ConfigureAwait(false);
        return true;
    }

    private bool Delete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Destino fora do ar ou arquivo em uso: sera podado na proxima build.
            _logger.LogDebug("Nao foi possivel remover {Path}: {Message}", path, exception.Message);
            return false;
        }
    }
}
