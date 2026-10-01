using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Hosting;

namespace UnityLocalCI.Cli;

/// <summary>
/// O BuildMaker sem janela.
///
/// E o mesmo servico da versao do Windows — mesmo Core, mesmos passos, mesmo
/// banco. O que nao existe aqui e a interface: no Ubuntu ele roda como unidade
/// do systemd, e quem conversa com ele e o terminal.
///
/// A escolha de nao portar a janela foi consciente. A interface sao 35 arquivos
/// de WinForms, que so existe no Windows, e um servidor Ubuntu de build nao tem
/// ninguem sentado na frente dele para olhar uma tela.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var comando = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
        var config = CaminhoDaConfiguracao.Resolver(ValorDe(args, "--config"));

        try
        {
            return comando switch
            {
                null or "servico" or "service" => await Servico(config),
                "verificar" => Verificar(config),
                "projetos" => Projetos(config),
                "buildar" => Buildar(config, args.SkipWhile(a => a.ToLowerInvariant() != "buildar").Skip(1)
                                                  .Where(a => !a.StartsWith('-')).ToArray()),
                "versao" or "--version" or "-v" => Versao(),
                "ajuda" or "--help" or "-h" => Ajuda(0),
                _ => Erro($"comando desconhecido: {comando}"),
            };
        }
        catch (FileNotFoundException excecao)
        {
            return Erro(excecao.Message);
        }
    }

    // ------------------------------------------------------------------ servico

    /// <summary>
    /// Sobe o host e fica de pe ate o systemd mandar parar.
    ///
    /// Nao ha AddSystemd equivalente ao AddWindowsService: o .NET so precisa
    /// dele para o protocolo de notificacao do Type=notify, e a unidade que o
    /// pacote instala e Type=simple. O SIGTERM que o systemd manda no stop ja
    /// chega ao host pelo tratamento padrao de encerramento.
    /// </summary>
    private static async Task<int> Servico(string config)
    {
        ExigirConfiguracao(config);

        // Uma vez, e so na primeira vez: projetos que ainda estejam dentro do
        // appsettings.json passam a ter arquivo proprio.
        ProjectFiles.MigrateFromAppSettings(config);

        var builder = Host.CreateApplicationBuilder();

        builder.Configuration
            .SetBasePath(Path.GetDirectoryName(config)!)
            .AddJsonFile(Path.GetFileName(config), optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("UNITYLOCALCI_");

        builder.Services.AddUnityLocalCI(builder.Configuration, ProjectFiles.FolderFor(config));

        var host = builder.Build();

        try
        {
            // Validacao forcada antes de o host subir: configuracao invalida sai
            // como lista de itens a corrigir, e nao como stack trace.
            _ = host.Services.GetRequiredService<IOptions<CiOptions>>().Value;
        }
        catch (OptionsValidationException excecao)
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BuildMaker");
            foreach (var falha in excecao.Failures)
                logger.LogCritical("Configuracao invalida: {Falha}", falha);

            return 1;
        }

        await host.RunAsync();
        return 0;
    }

    // ------------------------------------------------------------- diagnostico

    /// <summary>
    /// Valida sem subir nada. E o que roda depois de editar a configuracao a
    /// mao, e o que a unidade do systemd chama no ExecStartPre: assim uma
    /// configuracao quebrada aparece no 'systemctl status' como uma lista de
    /// problemas, e nao como um servico que reinicia em laco.
    /// </summary>
    private static int Verificar(string config)
    {
        ExigirConfiguracao(config);

        var opcoes = ConfigFile.Load(config, out var problemas);

        foreach (var problema in problemas)
            Console.Error.WriteLine($"  [!] {problema}");

        var validacao = new CiOptionsValidator(Cofre()).Validate(null, opcoes);

        if (validacao.Failed)
        {
            foreach (var falha in validacao.Failures!)
                Console.Error.WriteLine($"  [x] {falha}");

            Console.Error.WriteLine();
            Console.Error.WriteLine($"Configuracao invalida: {config}");
            return 1;
        }

        Console.WriteLine($"Configuracao valida: {config}");
        Console.WriteLine($"  {opcoes.Projects.Count} projeto(s), " +
                          $"{opcoes.Projects.Count(p => p.Enabled)} habilitado(s)");

        return problemas.Count > 0 ? 1 : 0;
    }

    private static int Projetos(string config)
    {
        ExigirConfiguracao(config);

        var opcoes = ConfigFile.Load(config, out _);
        if (opcoes.Projects.Count == 0)
        {
            Console.WriteLine("Nenhum projeto configurado.");
            return 0;
        }

        var largura = opcoes.Projects.Max(p => p.Name.Length);
        foreach (var projeto in opcoes.Projects)
        {
            var estado = projeto.Enabled ? "habilitado" : "desligado";
            var gatilho = string.IsNullOrWhiteSpace(projeto.ManualTriggerFile)
                ? "sem gatilho manual"
                : projeto.ManualTriggerFile;

            Console.WriteLine($"  {projeto.Name.PadRight(largura)}  {estado,-10}  {gatilho}");
        }

        return 0;
    }

    // ---------------------------------------------------------------- gatilho

    /// <summary>
    /// Toca o arquivo de gatilho, do mesmo jeito que o buildar-tudo.ps1 faz no
    /// Windows. Nao enfileira nada: quem decide continua sendo o scheduler, que
    /// respeita o teto de builds simultaneas. Tocar cinco arquivos nao dispara
    /// cinco builds ao mesmo tempo.
    /// </summary>
    private static int Buildar(string config, string[] nomes)
    {
        ExigirConfiguracao(config);

        var opcoes = ConfigFile.Load(config, out _);
        var alvos = opcoes.Projects.Where(p => p.Enabled).ToList();

        if (nomes.Length > 0)
        {
            alvos = alvos.Where(p => nomes.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();

            var desconhecidos = nomes
                .Where(n => !opcoes.Projects.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var nome in desconhecidos)
                Console.Error.WriteLine($"  [!] projeto desconhecido: {nome}");

            if (desconhecidos.Count > 0) return 1;
        }

        if (alvos.Count == 0)
        {
            Console.Error.WriteLine("Nenhum projeto habilitado para buildar.");
            return 1;
        }

        var tocados = 0;
        foreach (var projeto in alvos)
        {
            if (string.IsNullOrWhiteSpace(projeto.ManualTriggerFile))
            {
                Console.Error.WriteLine($"  [!] {projeto.Name}: sem ManualTriggerFile, ignorado.");
                continue;
            }

            var pasta = Path.GetDirectoryName(projeto.ManualTriggerFile);
            if (!string.IsNullOrEmpty(pasta)) Directory.CreateDirectory(pasta);

            // O servico apaga o arquivo ao consumir, entao o caso comum e ele nao
            // existir e precisar ser criado.
            File.WriteAllText(projeto.ManualTriggerFile, DateTimeOffset.Now.ToString("o"));
            Console.WriteLine($"  {projeto.Name}: gatilho acionado.");
            tocados++;
        }

        return tocados > 0 ? 0 : 1;
    }

    // ----------------------------------------------------------------- apoio

    private static int Versao()
    {
        var versao = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "desconhecida";
        Console.WriteLine($"BuildMaker {versao}");
        return 0;
    }

    private static int Ajuda(int codigo)
    {
        var saida = codigo == 0 ? Console.Out : Console.Error;
        saida.WriteLine("""
            BuildMaker — CI local para projetos Unity

            uso: buildmaker [comando] [--config <arquivo>]

              servico     roda o CI ate receber um sinal de parada (padrao)
              verificar   valida a configuracao e sai; 0 se estiver boa
              projetos    lista os projetos configurados
              buildar     aciona o gatilho manual de um projeto, ou de todos
              versao      mostra a versao
              ajuda       mostra isto

            A configuracao, quando --config nao e passado:
              /etc/buildmaker/appsettings.json
              ~/.config/BuildMaker/appsettings.json
            """);

        return codigo;
    }

    private static int Erro(string mensagem)
    {
        Console.Error.WriteLine(mensagem);
        Console.Error.WriteLine();
        return Ajuda(1);
    }

    private static void ExigirConfiguracao(string config)
    {
        if (File.Exists(config)) return;

        throw new FileNotFoundException(
            $"Configuracao nao encontrada: {config}{Environment.NewLine}" +
            $"Passe outra com --config, ou crie a partir do exemplo instalado em " +
            $"{CaminhoDaConfiguracao.PastaDoSistema}.");
    }

    /// <summary>
    /// O validador precisa do cofre para checar se as credenciais citadas na
    /// configuracao existem — ele nunca le o valor, so pergunta se esta la.
    /// </summary>
    private static Core.Secrets.ICredentialStore Cofre()
        => OperatingSystem.IsWindows()
            ? new Core.Secrets.WindowsCredentialStore()
            : new Core.Secrets.LinuxCredentialStore();

    private static string? ValorDe(string[] args, string nome)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(nome, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];

        return null;
    }
}
