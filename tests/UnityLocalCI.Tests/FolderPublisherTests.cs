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

    /// <summary>
    /// A pasta de destino tem os zips, e nada mais.
    ///
    /// Ja teve uma pasta latest\ com a ultima build descompactada ao lado deles.
    /// Este teste e o que garante que publicar nao volte a criar pasta nenhuma
    /// ali: o time abre esse diretorio para pegar a build, e o que estiver
    /// dentro dele precisa ser build.
    /// </summary>
    [Fact]
    public async Task Publicar_deixa_no_destino_so_o_zip()
    {
        var (publisher, context, artifact) = await SetupAsync();

        Directory.CreateDirectory(Path.Combine(context.BuildOutputPath, "Build"));
        await File.WriteAllTextAsync(Path.Combine(context.BuildOutputPath, "index.html"), "a build");

        var result = await publisher.PublishAsync(artifact, context, default);

        Assert.True(result.Success);
        Assert.Equal([artifact.Name], Directory.GetFiles(_destination).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(_destination));
        Assert.Empty(context.Warnings);
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
            NullLogger<FolderPublisher>.Instance);

        return (publisher, context, new FileInfo(zipPath));
    }
}
