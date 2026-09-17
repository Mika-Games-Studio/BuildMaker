using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.App;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Hosting;

// Um executavel, dois modos:
//   sem argumento   abre a janela, com o servico rodando dentro dela
//   --service       roda sem interface, para o Windows Service
//
// O servico e o mesmo nos dois casos: o que muda e so quem o hospeda.

var headless = args.Contains("--service", StringComparer.OrdinalIgnoreCase)
               || WindowsServiceHelpers.IsWindowsService();

return headless ? await RunHeadlessAsync(args) : RunWindow();

static async Task<int> RunHeadlessAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Configuration
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables("UNITYLOCALCI_");

    builder.Services.AddWindowsService(options => options.ServiceName = "UnityLocalCI");
    builder.Services.AddUnityLocalCI(builder.Configuration);

    var host = builder.Build();

    try
    {
        // Validacao forcada antes de o host subir: assim a configuracao invalida
        // sai como uma lista de itens a corrigir, e nao como um stack trace.
        _ = host.Services.GetRequiredService<IOptions<CiOptions>>().Value;
    }
    catch (OptionsValidationException exception)
    {
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        foreach (var failure in exception.Failures)
            logger.LogCritical("Configuracao invalida: {Failure}", failure);

        return 1;
    }

    await host.RunAsync();
    return 0;
}

static int RunWindow()
{
    ApplicationConfiguration.Initialize();

    // Modo escuro do proprio WinForms. E o que escurece o que o tema nao
    // alcanca: barras de rolagem, caixas de dialogo e menus de contexto, que
    // sao desenhados pelo Windows e nao pelo aplicativo. Se um dia a API mudar,
    // o pior caso e voltarem a ser claros — o resto da janela nao depende dela.
#pragma warning disable WFO5001 // A API ainda e marcada como experimental.
    try { Application.SetColorMode(SystemColorMode.Dark); }
    catch (Exception) { /* Windows sem suporte: segue com o tema proprio */ }
#pragma warning restore WFO5001

    var liveLog = new LiveLog();
    var configPath = ConfigFile.DefaultPath;
    var controller = new HostController(liveLog, configPath);

    using var form = new MainForm(controller, liveLog, configPath);
    using var tray = new TrayPresence(form, controller);
    form.Tray = tray;

    // O host sobe junto com a janela. Se a configuracao estiver invalida, ele
    // nao sobe e a janela mostra a lista de itens a corrigir, em vez de o
    // programa morrer sem dizer nada.
    _ = controller.StartAsync();

    Application.Run(form);

    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    return 0;
}

