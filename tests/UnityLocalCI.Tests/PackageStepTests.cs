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
/// O zip e o jogo: e ele que vai para o navegador, para a loja ou para quem
/// pediu a build. Arquivo do CI misturado com os arquivos do jogo confunde quem
/// recebe, e um portal que valide o pacote rejeita por causa disso. O manifest e
/// o launcher continuam existindo — na pasta de saida, de onde latest\ e copiada.
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

    private async Task<BuildContext> ExecutarAsync(bool launcher = true)
    {
        var project = TestProjects.Create(staging: Staging) with
        {
            Packaging = new UnityLocalCI.Core.Configuration.ResolvedPackaging(
                "{project}-{sha}.zip", launcher),
        };

        var context = new BuildContext
        {
            BuildId = 7,
            Project = project,
            Commit = new CommitInfo(new string('a', 40), "Fulano", "mensagem"),
            Trigger = BuildTrigger.Manual,
            StartedAt = DateTimeOffset.UnixEpoch,
            LogPath = Path.Combine(_root, "build-7.log"),
            BuildOutputPath = Saida,
            Git = new GitContext
            {
                WorkspacePath = project.Repository.WorkspacePath,
                RepositoryUrl = project.Repository.Url,
                Branch = project.Repository.Branch,
            },
        };

        var step = new PackageStep(new FakeClock(), NullLogger<PackageStep>.Instance);
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

    [Fact]
    public async Task O_zip_nao_leva_o_launcher_do_CI()
    {
        var entradas = EntradasDoZip(await ExecutarAsync());

        Assert.DoesNotContain(LauncherScript.LauncherFileName, entradas);
        Assert.DoesNotContain(LauncherScript.ServerFileName, entradas);
    }

    [Fact]
    public async Task O_zip_nao_leva_o_manifest_do_CI()
        => Assert.DoesNotContain("manifest.json", EntradasDoZip(await ExecutarAsync()));

    /// <summary>
    /// Fora do zip, mas nao perdidos: latest\ e copiada da pasta de saida, e e la
    /// que o rodar.bat serve — uma build WebGL nao abre por file://.
    /// </summary>
    [Fact]
    public async Task O_launcher_e_o_manifest_ficam_na_pasta_de_saida()
    {
        var context = await ExecutarAsync();

        Assert.True(File.Exists(Path.Combine(Saida, LauncherScript.LauncherFileName)));
        Assert.True(File.Exists(Path.Combine(Saida, LauncherScript.ServerFileName)));
        Assert.True(File.Exists(Path.Combine(Saida, "manifest.json")));
        Assert.Contains("\"buildId\": 7", await File.ReadAllTextAsync(Path.Combine(Saida, "manifest.json")));
    }

    [Fact]
    public async Task Com_o_launcher_desligado_ele_nao_e_escrito_em_lugar_nenhum()
    {
        var context = await ExecutarAsync(launcher: false);

        Assert.False(File.Exists(Path.Combine(Saida, LauncherScript.LauncherFileName)));
        Assert.DoesNotContain(LauncherScript.LauncherFileName, EntradasDoZip(context));
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
