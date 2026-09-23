using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Pipeline;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.Queue;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O conteudo do zip entregue.
///
/// O zip e o jogo: e o que vai para o navegador, para a loja ou para quem pediu
/// a build. Arquivo do CI misturado com os arquivos do jogo confunde quem
/// recebe, e um portal que valide o pacote rejeita por causa disso.
///
/// A unica forma de garantir isso e nao escrever nada na pasta de saida — e e
/// isso que estes testes fixam.
/// </summary>
public class PackageStepTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ulci-package-" + Guid.NewGuid().ToString("N"));

    private string Saida => Path.Combine(_root, "out");
    private string Staging => Path.Combine(_root, "staging");

    public PackageStepTests()
    {
        // O minimo que a validacao exige, e que e o que o Unity produz.
        Directory.CreateDirectory(Path.Combine(Saida, "Build"));
        File.WriteAllText(Path.Combine(Saida, "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(Saida, "Build", "jogo.wasm"), "wasm");
        File.WriteAllText(Path.Combine(Saida, "build-guid.txt"), "gerado pelo unity");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<BuildContext> ExecutarAsync()
    {
        var project = TestProjects.Create(staging: Staging) with
        {
            Packaging = new UnityLocalCI.Core.Configuration.ResolvedPackaging("{project}-{sha}.zip"),
        };

        var context = new BuildContext
        {
            BuildId = 7,
            Project = project,
            Commit = new CommitInfo(new string('a', 40), "Fulano", "mensagem"),
            Trigger = BuildTrigger.Manual,
            StartedAt = DateTimeOffset.UnixEpoch,
            BuildOutputPath = Saida,
            Git = new GitContext
            {
                WorkspacePath = project.Repository.WorkspacePath,
                RepositoryUrl = project.Repository.Url,
                Branch = project.Repository.Branch,
            },
        };

        var step = new PackageStep(NullLogger<PackageStep>.Instance);
        var result = await step.ExecuteAsync(context, default);

        Assert.True(result.Success, result.ErrorSummary);
        return context;
    }

    private IReadOnlyList<string> EntradasDoZip(BuildContext context)
    {
        using var zip = ZipFile.OpenRead(context.ArtifactPath!);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    [Fact]
    public async Task O_zip_leva_o_que_o_unity_produziu()
    {
        var entradas = EntradasDoZip(await ExecutarAsync());

        Assert.Contains("index.html", entradas);
        Assert.Contains("Build/jogo.wasm", entradas);
        Assert.Contains("build-guid.txt", entradas);
    }

    /// <summary>
    /// Nada e escrito na pasta de saida — e por isso o zip so tem o jogo.
    /// </summary>
    [Fact]
    public async Task A_pasta_de_saida_fica_como_o_unity_a_deixou()
    {
        var antes = Directory
            .GetFileSystemEntries(Saida, "*", SearchOption.AllDirectories)
            .Order()
            .ToArray();

        await ExecutarAsync();

        Assert.Equal(antes, Directory
            .GetFileSystemEntries(Saida, "*", SearchOption.AllDirectories)
            .Order()
            .ToArray());
    }

    /// <summary>
    /// Nomes de arquivos que o CI ja escreveu ali e nao escreve mais. Ficam
    /// citados para o dia em que alguem pensar em trazer de volta um deles.
    /// </summary>
    [Theory]
    [InlineData("rodar.bat")]
    [InlineData("_servidor.ps1")]
    [InlineData("manifest.json")]
    public async Task O_zip_nao_leva_arquivo_do_CI(string nome)
    {
        var context = await ExecutarAsync();

        Assert.DoesNotContain(nome, EntradasDoZip(context));
        Assert.False(File.Exists(Path.Combine(Saida, nome)));
    }

    [Fact]
    public async Task O_sha256_do_artefato_e_o_do_arquivo_entregue()
    {
        var context = await ExecutarAsync();

        Assert.Equal(
            await PackageStep.ComputeSha256Async(context.ArtifactPath!, default),
            context.ArtifactSha256);
    }
}
