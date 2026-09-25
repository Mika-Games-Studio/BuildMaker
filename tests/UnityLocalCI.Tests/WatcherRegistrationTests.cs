using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Hosting;
using UnityLocalCI.Core.Watching;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// Quem observa os projetos nasce dos arquivos da pasta 'projetos'.
///
/// Este teste existe por um defeito que passou em producao: quando cada projeto
/// virou um arquivo proprio, o registro dos watchers continuou lendo so a secao
/// 'Projects' do appsettings — que deixou de existir. O servico subia dizendo
/// "1 projeto(s)", porque essa contagem vem das opcoes ja configuradas, e mesmo
/// assim nao observava nada: nem commit novo, nem gatilho manual. Nenhuma build
/// jamais comecaria, e nada no log diria por que.
/// </summary>
public class WatcherRegistrationTests : IDisposable
{
    private readonly string _pasta = Path.Combine(
        Path.GetTempPath(), "unitylocalci-watchers-" + Guid.NewGuid().ToString("N")[..8]);

    private ProjectOptions Projeto(string nome) => new()
    {
        Name = nome,
        Enabled = true,
        Repository = new RepositoryOptions
        {
            Url = "https://example.invalid/" + nome,
            Branch = "HML",
            WorkspacePath = Path.Combine(_pasta, "workspace", nome),
        },
        ManualTriggerFile = Path.Combine(_pasta, "triggers", nome + ".txt"),
        Unity = new UnityOptions { EditorVersion = "6000.3.20f1" },
    };

    private ServiceProvider Montar(params ProjectOptions[] projetos)
    {
        Directory.CreateDirectory(_pasta);
        ProjectFiles.SaveAll(_pasta, projetos);

        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["State:DatabasePath"] = Path.Combine(_pasta, "state", "ci.db"),
                ["Defaults:Unity:ExecuteMethod"] = "Builder.PerformBuild",
                ["Defaults:Unity:TimeoutMinutes"] = "90",
                ["Defaults:Publishing:StagingFolder"] = Path.Combine(_pasta, "staging"),
                ["Defaults:Publishing:ArtifactFolder"] = Path.Combine(_pasta, "dest"),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddUnityLocalCI(configuracao, _pasta);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Cada_projeto_ganha_um_observador_de_git_e_um_de_gatilho()
    {
        using var sp = Montar(Projeto("CrashUnity"), Projeto("HumanXRobots"));

        var hospedados = sp.GetServices<IHostedService>().ToList();

        Assert.Equal(2, hospedados.Count(s => s is GitWatcher));
        Assert.Equal(2, hospedados.Count(s => s is ManualTriggerWatcher));
    }

    /// <summary>Projeto desligado nao e observado — e continua sendo configuravel.</summary>
    [Fact]
    public void Projeto_desligado_nao_ganha_observador()
    {
        var desligado = Projeto("Pausado");
        desligado.Enabled = false;

        using var sp = Montar(Projeto("Ativo"), desligado);

        var hospedados = sp.GetServices<IHostedService>().ToList();

        Assert.Equal(1, hospedados.Count(s => s is GitWatcher));
        Assert.Equal(1, hospedados.Count(s => s is ManualTriggerWatcher));
    }

    /// <summary>
    /// O registro tem que enxergar os mesmos projetos que as opcoes: se a
    /// contagem do log e a dos observadores divergirem, o servico volta a
    /// mentir sobre o que esta observando.
    /// </summary>
    [Fact]
    public void O_que_o_servico_anuncia_e_o_que_ele_observa()
    {
        using var sp = Montar(Projeto("Um"), Projeto("Dois"), Projeto("Tres"));

        var anunciados = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CiOptions>>().Value.Projects;
        var observados = sp.GetServices<IHostedService>().Count(s => s is GitWatcher);

        Assert.Equal(anunciados.Count, observados);
    }

    public void Dispose()
    {
        try { Directory.Delete(_pasta, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
