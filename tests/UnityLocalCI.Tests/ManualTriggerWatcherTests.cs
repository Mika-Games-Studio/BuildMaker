using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.Watching;
using Xunit;

namespace UnityLocalCI.Tests;

public class ManualTriggerWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "unitylocalci-tests", Guid.NewGuid().ToString("N"));

    private readonly string _triggerFile;
    private readonly FakeGitClient _git = new();
    private readonly RecordingScheduler _scheduler = new();
    private readonly InMemoryBuildStore _store = new();

    public ManualTriggerWatcherTests()
    {
        Directory.CreateDirectory(_root);
        _triggerFile = Path.Combine(_root, "crash.txt");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* limpeza best effort */ }
    }

    private ManualTriggerWatcher Create()
    {
        var project = TestProjects.Create() with { ManualTriggerFile = _triggerFile };

        var trigger = new BuildTriggerService(
            _git, _scheduler, _store, new FakeCredentialStore(), NullLogger<BuildTriggerService>.Instance);

        return new ManualTriggerWatcher(
            project, trigger, new FakeClock(), NullLogger<ManualTriggerWatcher>.Instance);
    }

    [Fact]
    public async Task Tocar_o_arquivo_enfileira_uma_build_do_head()
    {
        _git.RemoteHeadSha = new string('c', 40);
        await File.WriteAllTextAsync(_triggerFile, "");

        var buildId = await Create().ConsumeAsync(_triggerFile, default);

        Assert.NotNull(buildId);
        var job = Assert.Single(_scheduler.Enqueued);
        Assert.Equal(BuildTrigger.Manual, job.Trigger);
        Assert.Equal(new string('c', 40), job.Commit.Sha);
    }

    [Fact]
    public async Task Arquivo_e_apagado_ao_consumir()
    {
        await File.WriteAllTextAsync(_triggerFile, "");

        await Create().ConsumeAsync(_triggerFile, default);

        // Se ficasse, o gatilho dispararia em laco a cada varredura.
        Assert.False(File.Exists(_triggerFile));
    }

    [Fact]
    public async Task Sem_arquivo_nada_acontece()
    {
        var buildId = await Create().ConsumeAsync(_triggerFile, default);

        Assert.Null(buildId);
        Assert.Empty(_scheduler.Enqueued);
    }

    [Fact]
    public async Task Consumir_duas_vezes_enfileira_uma_build_so()
    {
        await File.WriteAllTextAsync(_triggerFile, "");

        var watcher = Create();
        await watcher.ConsumeAsync(_triggerFile, default);
        await watcher.ConsumeAsync(_triggerFile, default);

        Assert.Single(_scheduler.Enqueued);
    }

    [Fact]
    public async Task Gatilho_manual_ignora_o_debounce()
    {
        // O arquivo de gatilho existe para dizer "constroi agora". Esperar dois
        // minutos de silencio derrotaria o proposito.
        await File.WriteAllTextAsync(_triggerFile, "");

        var watcher = Create();
        await watcher.ConsumeAsync(_triggerFile, default);

        Assert.Single(_scheduler.Enqueued);
    }

    [Fact]
    public async Task Arquivo_em_uso_nao_e_consumido_agora()
    {
        await File.WriteAllTextAsync(_triggerFile, "");

        // Editor ainda com o arquivo aberto: a proxima varredura tenta de novo,
        // em vez de o gatilho se perder.
        using (var _ = new FileStream(_triggerFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var buildId = await Create().ConsumeAsync(_triggerFile, default);
            Assert.Null(buildId);
        }

        Assert.True(File.Exists(_triggerFile));
        await Create().ConsumeAsync(_triggerFile, default);
        Assert.Single(_scheduler.Enqueued);
    }
}
