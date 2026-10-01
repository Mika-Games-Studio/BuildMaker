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
internal static class Program
{
    /// <summary>
    /// STAThread nao e decoracao: as caixas de selecao de pasta e de arquivo do
    /// Windows sao objetos COM do shell, e COM do shell so pode ser chamado de
    /// uma thread STA. Numa thread MTA a chamada atravessa o marshalling do
    /// sistema e trava o aplicativo — e, com o shell no meio, arrasta o resto
    /// da maquina junto.
    ///
    /// E por isto que o ponto de entrada voltou a ser um Main de verdade, e nao
    /// instrucoes de nivel superior: o Main gerado a partir delas usava 'await',
    /// virava async, e um Main async nao pode ser STA.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // Instalar e desinstalar sao modos deste mesmo executavel, e nao
        // programas separados. E o que permite distribuir um arquivo so: quem
        // baixa abre, clica em Instalar na janela, e o Windows depois desinstala
        // chamando este binario de novo com --desinstalar.
        if (args.Contains("--desinstalar", StringComparer.OrdinalIgnoreCase))
            return Desinstalar(args.Contains("--silencioso", StringComparer.OrdinalIgnoreCase));

        if (args.Contains("--instalar", StringComparer.OrdinalIgnoreCase))
            return InstalarSemJanela(args);

        var headless = args.Contains("--service", StringComparer.OrdinalIgnoreCase)
                       || WindowsServiceHelpers.IsWindowsService();

        if (!headless) return RunWindow();

        // Fora da thread STA: o host nao precisa dela, e uma thread STA parada
        // esperando uma tarefa, sem bombear mensagens, e armadilha conhecida de
        // COM.
        return Task.Run(() => RunHeadlessAsync(args)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Desinstalacao chamada pelo Windows, por Configuracoes &gt; Aplicativos.
    ///
    /// Com --silencioso nao pergunta nada: e o QuietUninstallString, que o
    /// Windows usa quando desinstala sem interacao. Sem ele, confirma — a
    /// entrada em Aplicativos Instalados pode ser clicada sem querer.
    /// </summary>
    private static int Desinstalar(bool silencioso)
    {
        if (!silencioso)
        {
            var resposta = MessageBox.Show(
                $"Remover o {AppNames.Display} desta maquina?" + Environment.NewLine + Environment.NewLine +
                "O historico de builds e os artefatos ja gerados NAO serao apagados.",
                AppNames.Display, MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (resposta != DialogResult.Yes) return 1;
        }

        var passos = new List<string>();
        try
        {
            Instalacao.Desinstalar(passos.Add);
        }
        catch (Exception excecao)
        {
            if (!silencioso)
            {
                MessageBox.Show(
                    "Nao foi possivel concluir a remocao:" + Environment.NewLine + excecao.Message,
                    AppNames.Display, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Instalacao sem janela, para quem automatiza. O equivalente do
    /// --silent-install do RustDesk.
    /// </summary>
    private static int InstalarSemJanela(string[] args)
    {
        try
        {
            Instalacao.Instalar(
                _ => { },
                inicioAutomatico: !args.Contains("--sem-inicio-automatico", StringComparer.OrdinalIgnoreCase));

            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static async Task<int> RunHeadlessAsync(string[] args)
    {
        var configPath = ConfigFile.DefaultPath;

        // Uma vez, e so na primeira vez: projetos que ainda estejam dentro do
        // appsettings.json passam a ter arquivo proprio.
        ProjectFiles.MigrateFromAppSettings(configPath);

        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("UNITYLOCALCI_");

        builder.Services.AddWindowsService(options => options.ServiceName = "UnityLocalCI");
        builder.Services.AddUnityLocalCI(builder.Configuration, ProjectFiles.FolderFor(configPath));

        var host = builder.Build();

        try
        {
            // Validacao forcada antes de o host subir: assim a configuracao invalida
            // sai como uma lista de itens a corrigir, e nao como um stack trace.
            _ = host.Services.GetRequiredService<IOptions<CiOptions>>().Value;
        }
        catch (OptionsValidationException exception)
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("UnityLocalCI");
            foreach (var failure in exception.Failures)
                logger.LogCritical("Configuracao invalida: {Failure}", failure);

            return 1;
        }

        await host.RunAsync();
        return 0;
    }

    private static int RunWindow()
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

        RegistrarEncerramento(liveLog);

        using var form = new MainForm(controller, liveLog, configPath);
        using var tray = new TrayPresence(form, controller);
        form.Tray = tray;

        // Fora da thread da janela. O primeiro 'await' do StartAsync costuma
        // completar na hora, e nesse caso a montagem do host, a abertura do
        // banco e a criacao dos watchers rodariam aqui mesmo — com a janela
        // congelada ate o fim.
        _ = Task.Run(() => controller.StartAsync());

        Application.Run(form);

        // O contexto do WinForms sai de cena antes da espera: qualquer
        // continuacao que quisesse voltar para a thread da janela ficaria presa
        // para sempre, porque o laco de mensagens ja acabou. E com prazo, para
        // um encerramento lento nunca virar um processo que nao morre.
        SynchronizationContext.SetSynchronizationContext(null);

        return Task.Run(async () => await controller.DisposeAsync()).Wait(TimeSpan.FromSeconds(20))
            ? 0
            : 1;
    }

    /// <summary>
    /// Registra no log da janela por que o programa esta encerrando.
    ///
    /// O log vive so enquanto o programa vive, entao a linha do ProcessExit
    /// nunca sera lida por ninguem — e as outras tres serao. Uma excecao na
    /// thread da janela, ou uma tarefa que morreu sem dono, nao encerram o
    /// programa: elas aparecem aqui, na aba de log, com o processo ainda de pe.
    /// E disso que se precisa para entender o que acabou de acontecer.
    /// </summary>
    private static void RegistrarEncerramento(LiveLog log)
    {
        void Anotar(string o_que) => log.Add(new LogLine(
            DateTimeOffset.Now, LogLevel.Error, "Encerramento", o_que));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Anotar($"excecao nao tratada (encerrando: {e.IsTerminating}): {e.ExceptionObject}");

        // Excecao na thread da janela. O WinForms mostraria uma caixa de dialogo
        // e seguiria; registrar antes e o que permite saber que ela existiu.
        Application.ThreadException += (_, e) =>
            Anotar($"excecao na thread da janela: {e.Exception}");

        TaskScheduler.UnobservedTaskException += (_, e) =>
            Anotar($"tarefa com excecao sem dono: {e.Exception}");

        Application.ApplicationExit += (_, _) => Anotar("laco de mensagens encerrado (Application.Exit ou janela fechada)");

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Anotar("processo saindo");
    }
}
