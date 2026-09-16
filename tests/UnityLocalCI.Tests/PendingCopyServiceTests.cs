using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Pipeline;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

public class PendingCopyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "unitylocalci-tests", Guid.NewGuid().ToString("N"));

    private readonly string _destination;
    private readonly string _staging;
    private readonly InMemoryBuildStore _store = new();
    private readonly CiOptions _options;

    public PendingCopyServiceTests()
    {
        _destination = Path.Combine(_root, "destino");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_staging);

        _options = new CiOptions
        {
            Scheduler = new SchedulerOptions { GlobalStatusFile = null },
            Defaults = new ProjectDefaults
            {
                Unity = new UnityOptions { EditorVersion = "6000.0.47f1" },
                Publishing = new PublishingOptions { StagingFolder = _staging },
            },
        };

        _options.Projects.Add(new ProjectOptions
        {
            Name = "Crash",
            Repository = new RepositoryOptions
            {
                Url = "https://example.invalid/crash",
                Branch = "HML",
                WorkspacePath = @"C:\ci\workspace\crash",
            },
            Publishing = new PublishingOptions { ArtifactFolder = _destination },
        });
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* limpeza best effort */ }
    }

    private PendingCopyService Create() => new(
        _store,
        new ArtifactCopier(NullLogger<ArtifactCopier>.Instance),
        new NoopGlobalStatusWriter(),
        new StaticOptionsMonitor<CiOptions>(_options),
        NullLogger<PendingCopyService>.Instance);

    private async Task<long> SeedPendingAsync(string project = "Crash", bool comArquivo = true)
    {
        var id = await _store.CreateAsync(new BuildRecord
        {
            CommitSha = new string('a', 40),
            Project = project,
            Branch = "HML",
            Status = BuildStatus.Succeeded,
            QueuedAt = DateTimeOffset.UnixEpoch,
        }, default);

        var zip = Path.Combine(_staging, $"Crash-HML-2026-{id}.zip");
        string? sha = null;

        if (comArquivo)
        {
            await File.WriteAllTextAsync(zip, $"conteudo da build {id}");
            sha = await PackageStep.ComputeSha256Async(zip, default);
        }

        var record = await _store.GetAsync(id, default);
        await _store.UpdateAsync(record! with
        {
            Status = BuildStatus.Succeeded,
            FinishedAt = DateTimeOffset.UnixEpoch.AddMinutes(id),
            ArtifactPath = zip,
            ArtifactSha256 = sha,
            PublishStatus = PublishStatus.PendingCopy,
        }, default);

        return id;
    }

    [Fact]
    public async Task Reenvia_o_artefato_quando_o_destino_volta()
    {
        var id = await SeedPendingAsync();

        var sent = await Create().RetryAllAsync(default);

        Assert.Equal(1, sent);
        Assert.True(File.Exists(Path.Combine(_destination, $"Crash-HML-2026-{id}.zip")));

        var record = await _store.GetAsync(id, default);
        Assert.Equal(PublishStatus.Published, record!.PublishStatus);
        Assert.NotNull(record.PublishedPath);
    }

    [Fact]
    public async Task Staging_nao_e_apagado_pelo_reenvio()
    {
        var id = await SeedPendingAsync();

        await Create().RetryAllAsync(default);

        // Regra 6: nunca apagar o staging. Quem remove e a retencao, e so
        // depois de a copia estar confirmada.
        Assert.True(File.Exists(Path.Combine(_staging, $"Crash-HML-2026-{id}.zip")));
    }

    [Fact]
    public async Task Destino_ainda_fora_do_ar_mantem_a_pendencia()
    {
        var id = await SeedPendingAsync();

        // Um arquivo no lugar da pasta: o destino segue inalcancavel.
        await File.WriteAllTextAsync(_destination, "nao sou uma pasta");

        var sent = await Create().RetryAllAsync(default);

        Assert.Equal(0, sent);
        var record = await _store.GetAsync(id, default);
        Assert.Equal(PublishStatus.PendingCopy, record!.PublishStatus);
        Assert.True(File.Exists(Path.Combine(_staging, $"Crash-HML-2026-{id}.zip")));
    }

    [Fact]
    public async Task Artefato_que_sumiu_do_staging_nao_quebra_o_reenvio()
    {
        await SeedPendingAsync(comArquivo: false);

        var sent = await Create().RetryAllAsync(default);

        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task Reenvia_na_ordem_em_que_foram_construidas()
    {
        var primeira = await SeedPendingAsync();
        var segunda = await SeedPendingAsync();

        await Create().RetryAllAsync(default);

        var ordem = Directory.GetFiles(_destination, "*.zip")
            .OrderBy(f => File.GetCreationTimeUtc(f))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Equal(new[] { $"Crash-HML-2026-{primeira}.zip", $"Crash-HML-2026-{segunda}.zip" }, ordem);
    }

    [Fact]
    public async Task Republish_de_build_ja_publicada_avisa_em_vez_de_recopiar()
    {
        var id = await SeedPendingAsync();
        await Create().RetryAsync(id, default);

        var error = await Create().RetryAsync(id, default);

        Assert.NotNull(error);
        Assert.Contains("ja esta publicada", error);
    }

    [Fact]
    public async Task Republish_de_build_inexistente_diz_isso()
    {
        var error = await Create().RetryAsync(9999, default);

        Assert.NotNull(error);
        Assert.Contains("nao existe", error);
    }

    [Fact]
    public async Task Nada_pendente_nao_faz_nada()
    {
        Assert.Equal(0, await Create().RetryAllAsync(default));
    }
}

public sealed class NoopGlobalStatusWriter : IGlobalStatusWriter
{
    public Task WriteAsync(CancellationToken ct) => Task.CompletedTask;
}
