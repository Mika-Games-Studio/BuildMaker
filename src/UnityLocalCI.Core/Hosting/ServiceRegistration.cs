using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Notifications;
using UnityLocalCI.Core.Pipeline;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.Secrets;
using UnityLocalCI.Core.State;
using UnityLocalCI.Core.Unity;
using UnityLocalCI.Core.Watching;

namespace UnityLocalCI.Core.Hosting;

/// <summary>
/// Montagem do host, num lugar so.
///
/// A janela e o modo servico usam exatamente os mesmos servicos: o que muda e
/// so quem hospeda. Duplicar este registro faria os dois modos divergirem na
/// primeira mudanca, e a divergencia apareceria em producao.
/// </summary>
public static class ServiceRegistration
{
    /// <param name="projectsFolder">
    /// Pasta com um arquivo por projeto. Nula usa a pasta 'projetos' ao lado do
    /// executavel, que e onde o instalador a coloca.
    /// </param>
    public static void AddUnityLocalCI(
        this IServiceCollection services, IConfiguration configuration, string? projectsFolder = null)
    {
        var pastaDeProjetos = projectsFolder
                              ?? Path.Combine(AppContext.BaseDirectory, ProjectFiles.FolderName);

        services.AddOptions<CiOptions>()
            .Bind(configuration)

            // Os projetos vem dos arquivos da pasta, e nao da secao 'Projects'.
            // Um arquivo por projeto: mexer num deles nao reescreve os outros, e
            // um erro de digitacao num nao derruba a leitura de todos.
            .PostConfigure(options =>
            {
                var daPasta = ProjectFiles.LoadAll(pastaDeProjetos);
                if (daPasta.Count > 0 || Directory.Exists(pastaDeProjetos))
                    options.Projects = daPasta.ToList();
            })
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<CiOptions>, CiOptionsValidator>();

        // Infraestrutura
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISystemResources, WindowsSystemResources>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IUnityCliClient, UnityCliClient>();
        services.AddSingleton<IResourceGuard, SystemResourceGuard>();

        services.AddSingleton<IBuildStore>(sp => new SqliteBuildStore(
            sp.GetRequiredService<IOptions<CiOptions>>().Value.State.DatabasePath,
            sp.GetRequiredService<ILogger<SqliteBuildStore>>()));

        // Scheduler: singleton, exposto como interface e como hosted service.
        services.AddSingleton<BuildScheduler>();
        services.AddSingleton<IBuildScheduler>(sp => sp.GetRequiredService<BuildScheduler>());

        // Pipeline: escopo proprio por build, para que timeout ou travamento de
        // um projeto nao alcance os demais.
        services.AddScoped<IBuildLogWriter, BuildLogWriter>();
        services.AddScoped<IArtifactPublisher, FolderPublisher>();
        services.AddScoped<INotifier, LogNotifier>();
        services.AddScoped<INotifier, StatusFileNotifier>();
        services.AddScoped<INotifier, TeamsNotifier>();
        services.AddScoped<SyncStep>();
        services.AddScoped<UnityBuildStep>();
        services.AddScoped<PackageStep>();
        services.AddScoped<PublishStep>();
        services.AddScoped<IBuildRunner, BuildPipeline>();

        // Singleton e nao scoped: quem escreve e o pipeline, dentro do escopo da
        // build; quem le e a janela, fora dele.
        services.AddSingleton<BuildProgress>();

        services.AddSingleton<ILatestFolderWriter, LatestFolderWriter>();
        services.AddSingleton<IGlobalStatusWriter, GlobalStatusWriter>();
        services.AddSingleton<IRetentionService, RetentionService>();
        services.AddSingleton<ArtifactCopier>();
        services.AddSingleton<IPendingCopyService, PendingCopyService>();
        services.AddSingleton<BuildTriggerService>();
        services.AddSingleton<GitWatcherRegistry>();
        services.AddSingleton<OrphanRecovery>();
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });

        services.AddHostedService<StartupService>();
        services.AddHostedService(sp => sp.GetRequiredService<BuildScheduler>());

        AddPerProjectWatchers(services, configuration, pastaDeProjetos);

        services.AddHostedService<SignalListener>();
        services.AddHostedService<PendingCopyRetryWorker>();
    }

    /// <summary>
    /// Um GitWatcher e um ManualTriggerWatcher por projeto habilitado, com o
    /// polling espacado na inicializacao para que N projetos nao disparem
    /// 'git fetch' no mesmo instante.
    ///
    /// A lista de projetos e montada aqui do mesmo jeito que no PostConfigure,
    /// e nao so pelo binder: desde que cada projeto virou um arquivo proprio, a
    /// secao 'Projects' do appsettings nao existe mais. Ler so o binder deixava
    /// esta lista vazia — o servico subia dizendo "1 projeto(s)", porque o
    /// StartupService le as opcoes ja configuradas, e mesmo assim nao observava
    /// nada: nem commit novo, nem gatilho manual.
    /// </summary>
    private static void AddPerProjectWatchers(
        IServiceCollection services, IConfiguration configuration, string pastaDeProjetos)
    {
        var configured = configuration.Get<CiOptions>() ?? new CiOptions();

        var daPasta = ProjectFiles.LoadAll(pastaDeProjetos);
        if (daPasta.Count > 0 || Directory.Exists(pastaDeProjetos))
            configured.Projects = daPasta.ToList();

        var projects = ProjectResolver.ResolveEnabled(configured);

        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var offset = TimeSpan.FromSeconds(index * 7);

            services.AddSingleton<IHostedService>(sp =>
            {
                var watcher = new GitWatcher(
                    project,
                    offset,
                    sp.GetRequiredService<IGitClient>(),
                    sp.GetRequiredService<IBuildStore>(),
                    sp.GetRequiredService<BuildTriggerService>(),
                    sp.GetRequiredService<IClock>(),
                    sp.GetRequiredService<ILogger<GitWatcher>>());

                // O registro e o que permite ao sinal do hook, e a janela,
                // cutucarem o projeto certo.
                sp.GetRequiredService<GitWatcherRegistry>().Register(project.Name, watcher);
                return watcher;
            });

            services.AddSingleton<IHostedService>(sp => new ManualTriggerWatcher(
                project,
                sp.GetRequiredService<BuildTriggerService>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<ManualTriggerWatcher>>()));
        }
    }
}
