using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Queue;

namespace UnityLocalCI.Core.Watching;

/// <summary>
/// Disparo manual sem endpoint HTTP: cada projeto observa o arquivo apontado por
/// seu ManualTriggerFile. Criar ou tocar esse arquivo enfileira uma build do HEAD
/// atual, e o servico o apaga ao consumir.
///
/// Um atalho na area de trabalho por projeto resolve o caso de uso sem
/// infraestrutura nenhuma: sem porta aberta, sem servidor, sem liberacao de
/// firewall.
/// </summary>
public sealed class ManualTriggerWatcher : BackgroundService
{
    /// <summary>
    /// Intervalo da verificacao de seguranca. O FileSystemWatcher e quem da a
    /// resposta imediata, mas ele perde eventos em compartilhamento de rede e
    /// depois de o diretorio ser recriado, e um gatilho que some sem construir
    /// nada seria pior que um gatilho lento.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    private readonly ResolvedProject _project;
    private readonly BuildTriggerService _trigger;
    private readonly IClock _clock;
    private readonly ILogger<ManualTriggerWatcher> _logger;
    private readonly SemaphoreSlim _pending = new(0, 1);

    public ManualTriggerWatcher(
        ResolvedProject project,
        BuildTriggerService trigger,
        IClock clock,
        ILogger<ManualTriggerWatcher> logger)
    {
        _project = project;
        _trigger = trigger;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = _project.ManualTriggerFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogDebug("[{Project}] sem ManualTriggerFile configurado; disparo manual desligado.", _project.Name);
            return;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(folder)) return;

        Directory.CreateDirectory(folder);

        using var watcher = new FileSystemWatcher(folder, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        watcher.Created += (_, _) => Signal();
        watcher.Changed += (_, _) => Signal();
        watcher.Renamed += (_, _) => Signal();

        _logger.LogInformation("[{Project}] disparo manual observando {Path}.", _project.Name, path);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Quem chegar primeiro vence: o evento do FileSystemWatcher ou a
            // varredura periodica.
            await Task.WhenAny(
                _pending.WaitAsync(stoppingToken),
                _clock.Delay(SweepInterval, stoppingToken)).ConfigureAwait(false);

            if (stoppingToken.IsCancellationRequested) return;

            await ConsumeAsync(path!, stoppingToken).ConfigureAwait(false);
        }
    }

    private void Signal()
    {
        // O semaforo tem capacidade 1: varios eventos para o mesmo toque viram
        // um consumo so, que e o que se quer.
        try { _pending.Release(); } catch (SemaphoreFullException) { /* ja sinalizado */ }
    }

    internal async Task<long?> ConsumeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;

        // Apagar antes de enfileirar: se a build falhar, o arquivo ja saiu e o
        // gatilho nao fica disparando em laco. Tocar de novo e trivial.
        if (!TryDelete(path)) return null;

        _logger.LogInformation("[{Project}] gatilho manual acionado.", _project.Name);

        try
        {
            return await _trigger
                .EnqueueHeadAsync(_project, BuildTrigger.Manual, ct)
                .ConfigureAwait(false);
        }
        catch (GitCommandException exception)
        {
            _logger.LogWarning(
                "[{Project}] gatilho manual nao pode enfileirar: {Message}", _project.Name, exception.Message);
            return null;
        }
    }

    /// <summary>
    /// O arquivo pode estar aberto pelo editor que acabou de cria-lo. Nesse caso
    /// nao consumimos agora: a proxima varredura tenta de novo.
    /// </summary>
    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException exception)
        {
            _logger.LogDebug("[{Project}] gatilho ainda em uso: {Message}", _project.Name, exception.Message);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(
                "[{Project}] sem permissao para apagar o gatilho {Path}: {Message}",
                _project.Name, path, exception.Message);
            return false;
        }
    }
}
