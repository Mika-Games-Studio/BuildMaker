using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Pipeline;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.Queue;
using Xunit;

namespace UnityLocalCI.Tests;

public class FolderPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "unitylocalci-tests", Guid.NewGuid().ToString("N"));

    private readonly string _staging;
    private readonly string _destination;

    public FolderPublisherTests()
    {
        _staging = Path.Combine(_root, "staging");
        _destination = Path.Combine(_root, "destino");
        Directory.CreateDirectory(_staging);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* limpeza best effort */ }
    }

    [Fact]
    public async Task Publica_o_zip_com_o_nome_final()
    {
        var (publisher, context, artifact) = await SetupAsync();

        var result = await publisher.PublishAsync(artifact, context, default);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_destination, artifact.Name)));
    }

    [Fact]
    public async Task Copia_parcial_nunca_fica_com_o_nome_final()
    {
        var (publisher, context, artifact) = await SetupAsync();

        await publisher.PublishAsync(artifact, context, default);

        // Nao pode sobrar nenhum .part, e o arquivo final precisa estar integro.
        Assert.Empty(Directory.GetFiles(_destination, "*.part"));
        Assert.Equal(
            context.ArtifactSha256,
            await PackageStep.ComputeSha256Async(Path.Combine(_destination, artifact.Name), default));
    }

    [Fact]
    public async Task Sha_divergente_apos_a_copia_e_falha_de_publicacao()
    {
        var (publisher, context, artifact) = await SetupAsync();
        context.ArtifactSha256 = new string('0', 64);

        var result = await publisher.PublishAsync(artifact, context, default);

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.Error);
        // Nada com o nome final foi exposto.
        Assert.False(File.Exists(Path.Combine(_destination, artifact.Name)));
    }

    [Fact]
    public async Task Publicar_tambem_atualiza_a_pasta_latest()
    {
        var (publisher, context, artifact) = await SetupAsync();

        Directory.CreateDirectory(Path.Combine(context.BuildOutputPath, "Build"));
        await File.WriteAllTextAsync(Path.Combine(context.BuildOutputPath, "index.html"), "a build");
        await File.WriteAllTextAsync(Path.Combine(context.BuildOutputPath, "rodar.bat"), "@echo off");

        var result = await publisher.PublishAsync(artifact, context, default);

        Assert.True(result.Success);
        Assert.Equal("a build", await File.ReadAllTextAsync(Path.Combine(_destination, "latest", "index.html")));
        Assert.True(File.Exists(Path.Combine(_destination, "latest", "rodar.bat")));
        Assert.Empty(context.Warnings);
    }

    [Fact]
    public async Task Falha_na_latest_nao_derruba_a_publicacao_do_zip()
    {
        // A pasta da build nao existe: a latest\ falha, mas o zip ja esta no
        // destino e e ele que importa. A falha vira aviso no _STATUS.txt.
        var (publisher, context, artifact) = await SetupAsync();

        var result = await publisher.PublishAsync(artifact, context, default);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_destination, artifact.Name)));
        Assert.Contains(context.Warnings, w => w.Contains(@"latest\"));
    }

    [Fact]
    public async Task Destino_indisponivel_nao_descarta_o_artefato_do_staging()
    {
        var (publisher, context, artifact) = await SetupAsync();

        // Um arquivo no lugar da pasta faz a criacao do diretorio falhar, que e o
        // que acontece na pratica quando o compartilhamento esta fora do ar.
        await File.WriteAllTextAsync(_destination, "nao sou uma pasta");

        var result = await publisher.PublishAsync(artifact, context, default);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.True(File.Exists(artifact.FullName), "o zip precisa continuar no staging local");
    }

    private async Task<(FolderPublisher Publisher, BuildContext Context, FileInfo Artifact)> SetupAsync()
    {
        var zipPath = Path.Combine(_staging, "Crash-HML-20260914-a1b2c3d.zip");
        await File.WriteAllBytesAsync(zipPath, Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());

        var project = TestProjects.Create(staging: _staging, artifactFolder: _destination);

        var context = new BuildContext
        {
            BuildId = 42,
            Project = project,
            Commit = new CommitInfo(new string('a', 40), "Fulano", "mensagem"),
            Trigger = BuildTrigger.Poll,
            StartedAt = DateTimeOffset.UnixEpoch,
            LogPath = Path.Combine(_root, "build-42.log"),
            BuildOutputPath = Path.Combine(_root, "out"),
            Git = new GitContext
            {
                WorkspacePath = project.Repository.WorkspacePath,
                RepositoryUrl = project.Repository.Url,
                Branch = project.Repository.Branch,
            },
            ArtifactPath = zipPath,
            ArtifactSha256 = await PackageStep.ComputeSha256Async(zipPath, default),
        };

        var publisher = new FolderPublisher(
            new ArtifactCopier(NullLogger<ArtifactCopier>.Instance),
            new LatestFolderWriter(NullLogger<LatestFolderWriter>.Instance),
            NullLogger<FolderPublisher>.Instance);

        return (publisher, context, new FileInfo(zipPath));
    }
}
