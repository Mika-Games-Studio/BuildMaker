using System.Text.Json;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Hosting;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// Um arquivo de configuracao por projeto.
///
/// O teste que mais importa aqui e o da migracao: quem ja esta rodando tem os
/// projetos dentro do appsettings.json, e uma atualizacao que perdesse essa
/// configuracao seria pior que nao ter feito a mudanca.
/// </summary>
public class ProjectFilesTests : IDisposable
{
    private readonly string _raiz = Path.Combine(
        Path.GetTempPath(), "unitylocalci-projetos-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_raiz, "appsettings.json");
    private string Folder => Path.Combine(_raiz, ProjectFiles.FolderName);

    public ProjectFilesTests() => Directory.CreateDirectory(_raiz);

    private static ProjectOptions Projeto(string nome, string branch = "HML") => new()
    {
        Name = nome,
        Enabled = true,
        Repository = new RepositoryOptions
        {
            Url = $"https://github.com/OPAGames/{nome}.git",
            Branch = branch,
            WorkspacePath = @"C:\ci\workspace\" + nome,
            PatCredentialName = "UnityLocalCI_GitHubPat",
        },
        Unity = new UnityOptions { EditorVersion = "2022.3.62f3" },
        Publishing = new PublishingOptions { ArtifactFolder = @"D:\builds\" + nome },
    };

    [Fact]
    public void Cada_projeto_vira_um_arquivo_com_o_nome_dele()
    {
        ProjectFiles.SaveAll(Folder, [Projeto("CrashUnity"), Projeto("HumanXRobots")]);

        Assert.True(File.Exists(Path.Combine(Folder, "CrashUnity.json")));
        Assert.True(File.Exists(Path.Combine(Folder, "HumanXRobots.json")));

        var lidos = ProjectFiles.LoadAll(Folder);

        Assert.Equal(["CrashUnity", "HumanXRobots"], lidos.Select(p => p.Name));
        Assert.Equal("2022.3.62f3", lidos[0].Unity?.EditorVersion);
        Assert.Equal(@"D:\builds\CrashUnity", lidos[0].Publishing?.ArtifactFolder);
    }

    [Fact]
    public void Renomear_o_projeto_renomeia_o_arquivo_e_nao_deixa_o_antigo_para_tras()
    {
        ProjectFiles.SaveAll(Folder, [Projeto("Antigo")]);
        ProjectFiles.SaveAll(Folder, [Projeto("Novo")]);

        Assert.False(File.Exists(Path.Combine(Folder, "Antigo.json")));
        Assert.True(File.Exists(Path.Combine(Folder, "Novo.json")));
        Assert.Single(ProjectFiles.LoadAll(Folder));
    }

    [Fact]
    public void Remover_o_projeto_apaga_o_arquivo()
    {
        ProjectFiles.SaveAll(Folder, [Projeto("A"), Projeto("B")]);
        ProjectFiles.SaveAll(Folder, [Projeto("A")]);

        Assert.Equal(["A"], ProjectFiles.LoadAll(Folder).Select(p => p.Name));
    }

    /// <summary>
    /// Um arquivo quebrado nao pode levar os outros junto: o projeto vizinho
    /// sumiria da lista sem explicacao, e a build dele pararia em silencio.
    /// </summary>
    [Fact]
    public void Arquivo_invalido_vira_problema_relatado_e_nao_derruba_os_demais()
    {
        ProjectFiles.SaveAll(Folder, [Projeto("Bom")]);
        File.WriteAllText(Path.Combine(Folder, "Quebrado.json"), "{ isto nao e json");

        var projetos = ProjectFiles.LoadAll(Folder, out var problemas);

        Assert.Equal(["Bom"], projetos.Select(p => p.Name));
        Assert.Single(problemas);
        Assert.Contains("Quebrado.json", problemas[0]);
    }

    [Fact]
    public void Nome_com_caractere_proibido_ainda_vira_arquivo()
    {
        Assert.Equal("Crash-Avia-Turbo.json", ProjectFiles.FileNameFor("Crash/Avia:Turbo"));
        Assert.Equal("projeto.json", ProjectFiles.FileNameFor("   "));
    }

    // ------------------------------------------------------------- migracao

    private void EscreverConfigAntigo() => File.WriteAllText(ConfigPath, """
        {
          "Logging": { "LogLevel": { "Default": "Information" } },
          "Scheduler": { "MaxConcurrentBuilds": 3 },
          "State": { "DatabasePath": "C:\\ci\\state\\db.sqlite", "LogFolder": "C:\\ci\\logs" },
          "Defaults": { "Unity": { "BuildTarget": "WebGL" } },
          "Projects": [
            { "Name": "CrashUnity", "Enabled": true,
              "Repository": { "Url": "https://github.com/OPAGames/CrashUnity.git", "Branch": "crash-aviaturbo-hml" } },
            { "Name": "HumanXRobots", "Enabled": false,
              "Repository": { "Url": "https://github.com/Mika-Games-Studio/HumanXRobots.git", "Branch": "main" } }
          ],
          "Notifications": { "TeamsWebhookCredentialName": null }
        }
        """);

    [Fact]
    public void Migracao_move_os_projetos_para_arquivos_e_preserva_o_resto()
    {
        EscreverConfigAntigo();

        var movidos = ProjectFiles.MigrateFromAppSettings(ConfigPath);

        Assert.Equal(2, movidos);
        Assert.Equal(["CrashUnity", "HumanXRobots"], ProjectFiles.LoadAll(Folder).Select(p => p.Name));

        using var documento = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var raiz = documento.RootElement;

        Assert.False(raiz.TryGetProperty("Projects", out _));
        Assert.Equal(3, raiz.GetProperty("Scheduler").GetProperty("MaxConcurrentBuilds").GetInt32());
        Assert.Equal("WebGL", raiz.GetProperty("Defaults").GetProperty("Unity").GetProperty("BuildTarget").GetString());
        Assert.True(raiz.TryGetProperty("Logging", out _));
    }

    [Fact]
    public void Migracao_nao_repete_nem_sobrescreve_o_que_ja_esta_na_pasta()
    {
        EscreverConfigAntigo();
        Assert.Equal(2, ProjectFiles.MigrateFromAppSettings(ConfigPath));

        // Alguem editou um projeto depois da migracao.
        ProjectFiles.SaveAll(Folder, [Projeto("CrashUnity", branch: "outra-branch")]);

        Assert.Equal(0, ProjectFiles.MigrateFromAppSettings(ConfigPath));
        Assert.Equal("outra-branch", ProjectFiles.LoadAll(Folder).Single().Repository!.Branch);
    }

    [Fact]
    public void Configuracao_sem_projetos_nao_tem_o_que_migrar()
    {
        File.WriteAllText(ConfigPath, """{ "Scheduler": { "MaxConcurrentBuilds": 1 } }""");

        Assert.Equal(0, ProjectFiles.MigrateFromAppSettings(ConfigPath));
    }

    // -------------------------------------------------- pela porta do ConfigFile

    [Fact]
    public void Gravar_pela_janela_escreve_os_arquivos_e_nao_deixa_projetos_no_appsettings()
    {
        EscreverConfigAntigo();

        var options = ConfigFile.Load(ConfigPath);
        Assert.Equal(2, options.Projects.Count);

        options.Projects.RemoveAt(1);
        options.Projects[0].Repository!.WorkspacePath = @"C:\ci\workspace\CrashUnity";
        options.Projects[0].Repository!.PatCredentialName = null;
        options.Projects[0].Unity = new UnityOptions { EditorVersion = "2022.3.62f3", ExecuteMethod = "Builder.PerformBuild", TimeoutMinutes = 90 };
        options.Projects[0].Retention = new RetentionOptions { KeepLastBuilds = 10 };

        // As pastas sao dos padroes, nao do projeto: a de destino e uma so para
        // todos, e o staging tambem.
        options.Defaults.Publishing = new PublishingOptions
        {
            StagingFolder = @"C:\ci\staging",
            ArtifactFolder = @"D:\builds",
        };

        var problemas = ConfigFile.Save(ConfigPath, options, new FakeCredentialStore());

        Assert.Empty(problemas);
        Assert.Equal(["CrashUnity"], ProjectFiles.LoadAll(Folder).Select(p => p.Name));
        Assert.DoesNotContain("Projects", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
        Assert.Single(ConfigFile.Load(ConfigPath).Projects);
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
        GC.SuppressFinalize(this);
    }
}
