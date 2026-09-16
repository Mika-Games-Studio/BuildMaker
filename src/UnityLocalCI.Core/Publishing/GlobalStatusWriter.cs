using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Publishing;

public interface IGlobalStatusWriter
{
    /// <summary>Reescreve o _STATUS-GERAL.txt com o estado de todos os projetos.</summary>
    Task WriteAsync(CancellationToken ct);
}

/// <summary>
/// O _STATUS-GERAL.txt consolida todos os projetos num arquivo so, na raiz do
/// compartilhamento. Reescrito sempre que qualquer build termina ou entra em
/// execucao.
///
/// E singleton e serializa as escritas: duas builds de projetos diferentes podem
/// terminar no mesmo instante, e sem o lock uma sobrescreveria a leitura da
/// outra, produzindo um arquivo que descreve um estado que nunca existiu.
/// </summary>
public sealed class GlobalStatusWriter : IGlobalStatusWriter
{
    private readonly IBuildStore _store;
    private readonly IBuildScheduler _scheduler;
    private readonly IOptionsMonitor<CiOptions> _options;
    private readonly IClock _clock;
    private readonly ILogger<GlobalStatusWriter> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GlobalStatusWriter(
        IBuildStore store,
        IBuildScheduler scheduler,
        IOptionsMonitor<CiOptions> options,
        IClock clock,
        ILogger<GlobalStatusWriter> logger)
    {
        _store = store;
        _scheduler = scheduler;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task WriteAsync(CancellationToken ct)
    {
        var configuration = _options.CurrentValue;
        var path = configuration.Scheduler.GlobalStatusFile;

        if (string.IsNullOrWhiteSpace(path)) return;

        // Tudo sob o lock, leitura inclusive: o objetivo e que o arquivo reflita
        // um instante coerente, nao apenas que a escrita nao se intercale.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = await BuildRowsAsync(configuration, ct).ConfigureAwait(false);
            var snapshot = _scheduler.Snapshot();

            // O numero de builds em execucao vem das linhas, nao do contador do
            // scheduler: este arquivo e reescrito de dentro do pipeline, quando a
            // build que acabou ainda ocupa a vaga, e o rodape diria "1 em execucao"
            // logo abaixo de uma linha marcada como FALHOU.
            var running = rows.Count(r => r.IsRunning);

            var content = StatusFormatter.FormatGlobalStatus(
                _clock.UtcNow, rows, snapshot.Waiting, running, snapshot.MaxConcurrentBuilds);

            await AtomicFile.WriteAllTextAsync(path!, content, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // O destino pode ser um compartilhamento fora do ar. Isso nunca pode
            // derrubar uma build: o artefato e o que importa, o arquivo de status
            // sera reescrito na proxima.
            _logger.LogWarning("Nao foi possivel escrever {Path}: {Message}", path, exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<GlobalStatusRow>> BuildRowsAsync(CiOptions configuration, CancellationToken ct)
    {
        var running = await _store.GetByStatusAsync(BuildStatus.Running, ct).ConfigureAwait(false);
        var runningByProject = running
            .GroupBy(b => b.Project, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.Id).First(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<GlobalStatusRow>();

        foreach (var project in ProjectResolver.ResolveEnabled(configuration))
        {
            var lastFinished = await _store.GetLastFinishedAsync(project.Name, ct).ConfigureAwait(false);
            var current = runningByProject.GetValueOrDefault(project.Name);

            rows.Add(new GlobalStatusRow(
                Project: project.Name,
                IsRunning: current is not null,
                RunningSince: current?.StartedAt,
                LastFinished: lastFinished));
        }

        return rows;
    }
}
