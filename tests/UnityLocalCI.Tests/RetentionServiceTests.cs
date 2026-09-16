using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

public class RetentionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "unitylocalci-tests", Guid.NewGuid().ToString("N"));

    private readonly string _destination;
    private readonly string _staging;
    private readonly string _logs;
    private readonly InMemoryBuildStore _store = new();

    public RetentionServiceTests()
    {
        _destination = Path.Combine(_root, "destino");
        _staging = Path.Combine(_root, "staging");
        _logs = Path.Combine(_root, "logs");

        Directory.CreateDirectory(_destination);
        Directory.CreateDirectory(_staging);
        Directory.CreateDirectory(_logs);
        Directory.CreateDirectory(Path.Combine(_destination, "_logs"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* limpeza best effort */ }
    }

    private RetentionService Create() => new(_store, NullLogger<RetentionService>.Instance);

    private ResolvedProject Project(int keepLastBuilds = 10)
        => TestProjects.Create(staging: _staging, artifactFolder: _destination) with
        {
            Retention = new ResolvedRetention(keepLastBuilds, 50),
        };

    /// <summary>Cria uma build com os arquivos que ela teria gerado de verdade.</summary>
    private async Task<long> SeedAsync(
        BuildStatus status = BuildStatus.Succeeded,
        PublishStatus publish = PublishStatus.Published)
    {
        var id = await _store.CreateAsync(new BuildRecord
        {
            CommitSha = new string('a', 40),
            Project = "Crash",
            Branch = "HML",
            Status = status,
            QueuedAt = DateTimeOffset.UnixEpoch,
        }, default);

        var zipName = $"Crash-HML-2026-{id}.zip";
        var published = Path.Combine(_destination, zipName);
        var staged = Path.Combine(_staging, zipName);
        var log = Path.Combine(_logs, $"build-{id}.log");
        var copiedLog = Path.Combine(_destination, "_logs", $"build-{id}.log");

        foreach (var file in new[] { published, staged, log, copiedLog })
            await File.WriteAllTextAsync(file, "conteudo");

        var record = await _store.GetAsync(id, default);
        await _store.UpdateAsync(record! with
        {
            Status = status,
            FinishedAt = DateTimeOffset.UnixEpoch.AddMinutes(id),
            PublishedPath = published,
            ArtifactPath = staged,
            PublishStatus = publish,
            LogPath = log,
        }, default);

        return id;
    }

    private int ZipsNoDestino() => Directory.GetFiles(_destination, "*.zip").Length;

    [Fact]
    public async Task Mantem_exatamente_keep_last_builds()
    {
        for (var i = 0; i < 11; i++) await SeedAsync();

        var pruned = await Create().ApplyAsync(Project(keepLastBuilds: 10), default);

        Assert.Equal(1, pruned);
        Assert.Equal(10, ZipsNoDestino());
    }

    [Fact]
    public async Task A_build_mais_antiga_e_a_que_sai()
    {
        var primeira = await SeedAsync();
        for (var i = 0; i < 10; i++) await SeedAsync();

        await Create().ApplyAsync(Project(keepLastBuilds: 10), default);

        Assert.False(File.Exists(Path.Combine(_destination, $"Crash-HML-2026-{primeira}.zip")));
    }

    [Fact]
    public async Task Poda_apaga_zip_do_staging_e_os_dois_logs()
    {
        var antiga = await SeedAsync();
        for (var i = 0; i < 2; i++) await SeedAsync();

        await Create().ApplyAsync(Project(keepLastBuilds: 2), default);

        Assert.False(File.Exists(Path.Combine(_staging, $"Crash-HML-2026-{antiga}.zip")));
        Assert.False(File.Exists(Path.Combine(_logs, $"build-{antiga}.log")));
        Assert.False(File.Exists(Path.Combine(_destination, "_logs", $"build-{antiga}.log")));
    }

    [Fact]
    public async Task Registro_para_de_apontar_para_arquivo_apagado()
    {
        var antiga = await SeedAsync();
        for (var i = 0; i < 2; i++) await SeedAsync();

        await Create().ApplyAsync(Project(keepLastBuilds: 2), default);

        // Senao o _HISTORICO.txt continuaria oferecendo um zip que nao existe mais.
        var record = await _store.GetAsync(antiga, default);
        Assert.Null(record!.PublishedPath);
        Assert.Null(record.ArtifactPath);
    }

    [Fact]
    public async Task Copia_pendente_nunca_perde_o_artefato_do_staging()
    {
        // Regra 6: nunca apagar o staging antes de confirmar a copia. Uma build
        // com copia pendente so tem o artefato ali.
        var pendente = await SeedAsync(publish: PublishStatus.PendingCopy);
        for (var i = 0; i < 5; i++) await SeedAsync();

        await Create().ApplyAsync(Project(keepLastBuilds: 2), default);

        Assert.True(File.Exists(Path.Combine(_staging, $"Crash-HML-2026-{pendente}.zip")));
    }

    [Fact]
    public async Task Sequencia_de_falhas_nao_empurra_para_fora_a_ultima_build_boa()
    {
        var boa = await SeedAsync();
        for (var i = 0; i < 8; i++) await SeedAsync(status: BuildStatus.Failed);

        await Create().ApplyAsync(Project(keepLastBuilds: 1), default);

        // A contagem e de builds bem-sucedidas: o unico zip que de fato funciona
        // nao pode sumir so porque houve muita falha depois dele.
        Assert.True(File.Exists(Path.Combine(_destination, $"Crash-HML-2026-{boa}.zip")));
    }

    [Fact]
    public async Task Build_que_acabou_de_falhar_mantem_o_log()
    {
        // Regressao: a retencao contava so builds bem-sucedidas, entao uma
        // falha era podada na mesma execucao que a criou. O _STATUS.txt mandava
        // abrir "ver _logs\build-1.log" e o arquivo ja nao existia.
        var id = await SeedAsync(status: BuildStatus.Failed);

        await Create().ApplyAsync(Project(keepLastBuilds: 10), default);

        Assert.True(File.Exists(Path.Combine(_logs, $"build-{id}.log")),
            "o log da build recem-falhada precisa sobreviver a retencao");
        Assert.True(File.Exists(Path.Combine(_destination, "_logs", $"build-{id}.log")));
    }

    [Fact]
    public async Task Falhas_recentes_mantem_o_log_mesmo_sem_nenhuma_build_boa()
    {
        var ids = new List<long>();
        for (var i = 0; i < 3; i++) ids.Add(await SeedAsync(status: BuildStatus.Failed));

        await Create().ApplyAsync(Project(keepLastBuilds: 10), default);

        foreach (var id in ids)
            Assert.True(File.Exists(Path.Combine(_logs, $"build-{id}.log")));
    }

    [Fact]
    public async Task Falhas_antigas_ainda_sao_podadas()
    {
        var antiga = await SeedAsync(status: BuildStatus.Failed);
        for (var i = 0; i < 5; i++) await SeedAsync();

        await Create().ApplyAsync(Project(keepLastBuilds: 2), default);

        // O piso e por contagem, nao "falha nunca e podada": manter falhas para
        // sempre encheria o disco com o que ninguem vai ler.
        Assert.False(File.Exists(Path.Combine(_logs, $"build-{antiga}.log")));
    }

    [Fact]
    public async Task Nada_a_podar_nao_mexe_em_nada()
    {
        for (var i = 0; i < 3; i++) await SeedAsync();

        var pruned = await Create().ApplyAsync(Project(keepLastBuilds: 10), default);

        Assert.Equal(0, pruned);
        Assert.Equal(3, ZipsNoDestino());
    }

    [Fact]
    public async Task Poda_nao_toca_em_arquivos_que_nao_sao_de_build()
    {
        // A poda e guiada pelo banco, e nao por varredura da pasta: nada de
        // _STATUS.txt, latest\ ou zip que alguem copiou para la na mao.
        await File.WriteAllTextAsync(Path.Combine(_destination, "_STATUS.txt"), "status");
        await File.WriteAllTextAsync(Path.Combine(_destination, "copiado-na-mao.zip"), "zip alheio");
        Directory.CreateDirectory(Path.Combine(_destination, "latest"));

        for (var i = 0; i < 12; i++) await SeedAsync();
        await Create().ApplyAsync(Project(keepLastBuilds: 1), default);

        Assert.True(File.Exists(Path.Combine(_destination, "_STATUS.txt")));
        Assert.True(File.Exists(Path.Combine(_destination, "copiado-na-mao.zip")));
        Assert.True(Directory.Exists(Path.Combine(_destination, "latest")));
    }
}
