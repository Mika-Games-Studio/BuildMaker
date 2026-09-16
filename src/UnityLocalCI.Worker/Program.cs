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

    var liveLog = new LiveLog();
    var configPath = ConfigFile.DefaultPath;
    var controller = new HostController(liveLog, configPath);

    using var form = new MainForm(controller, liveLog, configPath);
    using var tray = new TrayPresence(form, controller);

    // O host sobe junto com a janela. Se a configuracao estiver invalida, ele
    // nao sobe e a janela mostra a lista de itens a corrigir, em vez de o
    // programa morrer sem dizer nada.
    _ = controller.StartAsync();

    Application.Run(form);

    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    return 0;
}

/// <summary>
/// Icone na bandeja. Fechar a janela pelo X a esconde: o servico continua
/// construindo, que e o que se espera de um CI. Sair de verdade e so pelo menu
/// da bandeja, e ai o aviso de que as builds param e explicito.
/// </summary>
internal sealed class TrayPresence : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Form _form;
    private readonly HostController _controller;

    public TrayPresence(Form form, HostController controller)
    {
        _form = form;
        _controller = controller;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => Show());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Exit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "UnityLocalCI",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => Show();
        _controller.StateChanged += UpdateTooltip;

        UpdateTooltip();
    }

    private void Show()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void Exit()
    {
        if (_controller.State == HostState.Rodando)
        {
            var resposta = MessageBox.Show(
                _form,
                "Sair encerra o servico: nenhuma build sera disparada enquanto o programa estiver fechado.\n\n" +
                "Para o CI rodar sem a janela aberta, registre-o como servico do Windows com tools\\install-service.ps1.\n\n" +
                "Sair mesmo assim?",
                "UnityLocalCI", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (resposta != DialogResult.Yes) return;
        }

        _icon.Visible = false;
        Application.Exit();
    }

    private void UpdateTooltip()
    {
        var texto = _controller.State switch
        {
            HostState.Rodando => "UnityLocalCI — em execucao",
            HostState.Iniciando => "UnityLocalCI — iniciando",
            HostState.Parado => "UnityLocalCI — parado",
            _ => "UnityLocalCI — falhou ao iniciar",
        };

        // O NotifyIcon trunca em 63 caracteres e lanca acima disso.
        _icon.Text = texto.Length > 63 ? texto[..63] : texto;
    }

    public void Dispose()
    {
        _controller.StateChanged -= UpdateTooltip;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
