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
using UnityLocalCI.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("UNITYLOCALCI_");

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var services = builder.Services;

services.AddOptions<CiOptions>()
    .Bind(builder.Configuration)
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

// Pipeline: escopo proprio por build, para que timeout ou travamento de um
// projeto nao alcance os demais.
services.AddScoped<IBuildLogWriter, BuildLogWriter>();
services.AddScoped<IArtifactPublisher, FolderPublisher>();
services.AddScoped<INotifier, LogNotifier>();
services.AddScoped<INotifier, StatusFileNotifier>();
services.AddSingleton<IGlobalStatusWriter, GlobalStatusWriter>();
services.AddScoped<SyncStep>();
services.AddScoped<UnityBuildStep>();
services.AddScoped<PackageStep>();
services.AddScoped<PublishStep>();
services.AddScoped<IBuildRunner, BuildPipeline>();

services.AddSingleton<OrphanRecovery>();

// Inicializacao do estado e recuperacao de orfas antes de qualquer watcher subir.
services.AddHostedService<StartupService>();
services.AddHostedService(sp => sp.GetRequiredService<BuildScheduler>());

// Um GitWatcher por projeto habilitado, com polling espacado na inicializacao
// para que N projetos nao disparem 'git fetch' no mesmo instante.
var configured = builder.Configuration.Get<CiOptions>() ?? new CiOptions();
var enabledProjects = ProjectResolver.ResolveEnabled(configured);

for (var index = 0; index < enabledProjects.Count; index++)
{
    var project = enabledProjects[index];
    var offset = TimeSpan.FromSeconds(index * 7);

    services.AddSingleton<IHostedService>(sp => new GitWatcher(
        project,
        offset,
        sp.GetRequiredService<IGitClient>(),
        sp.GetRequiredService<IBuildScheduler>(),
        sp.GetRequiredService<IBuildStore>(),
        sp.GetRequiredService<ICredentialStore>(),
        sp.GetRequiredService<IClock>(),
        sp.GetRequiredService<ILogger<GitWatcher>>()));
}

var host = builder.Build();

try
{
    // Validacao forcada antes de o host subir: assim a configuracao invalida sai
    // como uma lista de itens a corrigir, e nao como um stack trace do host.
    _ = host.Services.GetRequiredService<IOptions<CiOptions>>().Value;
}
catch (OptionsValidationException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Configuracao invalida. Corrija os itens abaixo em appsettings.json:");
    foreach (var failure in ex.Failures)
        Console.Error.WriteLine($"  - {failure}");
    Console.Error.WriteLine();
    return 1;
}

await host.RunAsync();
return 0;
