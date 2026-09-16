using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Queue;
using Xunit;

namespace UnityLocalCI.Tests;

public class ConfigurationTests
{
    private static CiOptions Sample()
    {
        var options = new CiOptions
        {
            Scheduler = new SchedulerOptions { MaxConcurrentBuilds = 2, MinFreeRamGb = 12 },
            Defaults = new ProjectDefaults
            {
                Watcher = new WatcherOptions { PollIntervalSeconds = 60, DebounceSeconds = 120 },
                Unity = new UnityOptions { EditorVersion = "6000.0.47f1", TimeoutMinutes = 90 },
                Publishing = new PublishingOptions { StagingFolder = @"C:\ci\staging" },
                Retention = new RetentionOptions { KeepLastBuilds = 10, MinFreeDiskGb = 50 },
            },
        };

        options.Projects.Add(new ProjectOptions
        {
            Name = "Crash",
            Repository = new RepositoryOptions
            {
                Url = "https://example.invalid/crash",
                Branch = "HML",
                WorkspacePath = @"C:\ci\workspace\crash",
            },
            Publishing = new PublishingOptions { ArtifactFolder = @"C:\ci\artifacts\crash" },
        });

        options.Projects.Add(new ProjectOptions
        {
            Name = "Mines",
            Repository = new RepositoryOptions
            {
                Url = "https://example.invalid/mines",
                Branch = "HML",
                WorkspacePath = @"C:\ci\workspace\mines",
            },
            // Projetos diferentes costumam estar em versoes diferentes do editor.
            Unity = new UnityOptions { EditorVersion = "6000.0.32f1" },
            Publishing = new PublishingOptions { ArtifactFolder = @"C:\ci\artifacts\mines" },
        });

        return options;
    }

    [Fact]
    public void Projeto_herda_defaults_e_sobrescreve_so_o_que_difere()
    {
        var resolved = ProjectResolver.ResolveEnabled(Sample());

        var crash = resolved.Single(p => p.Name == "Crash");
        var mines = resolved.Single(p => p.Name == "Mines");

        Assert.Equal("6000.0.47f1", crash.Unity.EditorVersion);
        Assert.Equal("6000.0.32f1", mines.Unity.EditorVersion);

        // O resto vem de Defaults nos dois.
        Assert.Equal(120, mines.Watcher.DebounceSeconds);
        Assert.Equal(90, mines.Unity.TimeoutMinutes);
        Assert.Equal(@"C:\ci\staging", mines.Publishing.StagingFolder);
    }

    [Fact]
    public void Projeto_desabilitado_fica_de_fora()
    {
        var options = Sample();
        options.Projects[1].Enabled = false;

        var resolved = ProjectResolver.ResolveEnabled(options);

        Assert.Equal(new[] { "Crash" }, resolved.Select(p => p.Name));
    }

    [Fact]
    public void Credencial_ausente_reprova_a_configuracao_com_mensagem_acionavel()
    {
        var options = Sample();
        options.Projects[0].Repository.PatCredentialName = "UnityLocalCI_NaoExiste";

        var result = new CiOptionsValidator(new FakeCredentialStore()).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("UnityLocalCI_NaoExiste") && f.Contains("cmdkey"));
    }

    [Fact]
    public void Credencial_presente_passa_na_validacao()
    {
        var options = Sample();
        options.Projects[0].Repository.PatCredentialName = "UnityLocalCI_Pat";

        var credentials = new FakeCredentialStore();
        credentials.Set("UnityLocalCI_Pat", "valor-do-pat");

        var result = new CiOptionsValidator(credentials).Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Dois_projetos_no_mesmo_workspace_sao_reprovados()
    {
        var options = Sample();
        options.Projects[1].Repository.WorkspacePath = options.Projects[0].Repository.WorkspacePath;

        var result = new CiOptionsValidator(new FakeCredentialStore()).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Library"));
    }

    [Fact]
    public void Versao_do_editor_ausente_e_reprovada_em_vez_de_adivinhada()
    {
        var options = Sample();
        options.Defaults.Unity.EditorVersion = null;
        options.Projects[0].Unity = null;

        var result = new CiOptionsValidator(new FakeCredentialStore()).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("EditorVersion"));
    }

    [Fact]
    public void Guarda_de_recursos_adia_quando_a_ram_livre_esta_abaixo_do_minimo()
    {
        var options = Sample();
        var project = ProjectResolver.ResolveEnabled(options)[0];

        var resources = new FakeSystemResources { FreePhysicalMemoryGb = 4 };
        var guard = new SystemResourceGuard(resources, new StaticOptionsMonitor<CiOptions>(options));

        var check = guard.Check(project);

        Assert.False(check.CanStart);
        Assert.Contains("RAM", check.Reason);
    }

    [Fact]
    public void Guarda_de_recursos_adia_quando_falta_disco()
    {
        var options = Sample();
        var project = ProjectResolver.ResolveEnabled(options)[0];

        var resources = new FakeSystemResources { FreeDiskGbValue = 10 };
        var guard = new SystemResourceGuard(resources, new StaticOptionsMonitor<CiOptions>(options));

        var check = guard.Check(project);

        Assert.False(check.CanStart);
        Assert.Contains("espaco livre", check.Reason);
    }

    [Fact]
    public void Guarda_de_recursos_libera_quando_ha_folga()
    {
        var options = Sample();
        var project = ProjectResolver.ResolveEnabled(options)[0];

        var guard = new SystemResourceGuard(new FakeSystemResources(), new StaticOptionsMonitor<CiOptions>(options));

        Assert.True(guard.Check(project).CanStart);
    }
}
