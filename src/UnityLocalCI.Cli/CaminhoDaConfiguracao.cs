namespace UnityLocalCI.Cli;

/// <summary>
/// Onde a configuracao mora, quando ninguem passou <c>--config</c>.
///
/// No Windows ela fica ao lado do executavel, porque a instalacao e por usuario
/// e a janela grava ali. No Linux isso seria errado: o pacote .deb instala em
/// /opt, que pertence ao root e e leitura para o resto do mundo — o servico nao
/// conseguiria gravar, e um upgrade do pacote apagaria o arquivo junto.
///
/// A ordem abaixo segue o costume do Linux: o que o administrador configurou
/// para a maquina inteira ganha do que veio no pacote.
/// </summary>
internal static class CaminhoDaConfiguracao
{
    public const string PastaDoSistema = "/etc/buildmaker";

    public static string Resolver(string? informado)
    {
        if (!string.IsNullOrWhiteSpace(informado)) return Path.GetFullPath(informado);

        var aoLadoDoBinario = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (OperatingSystem.IsWindows()) return aoLadoDoBinario;

        var doSistema = Path.Combine(PastaDoSistema, "appsettings.json");
        if (File.Exists(doSistema)) return doSistema;

        // Um usuario sem root tambem precisa conseguir rodar — em teste, ou numa
        // maquina onde ele nao instala nada em /etc.
        var doUsuario = Path.Combine(PastaDeConfiguracaoDoUsuario(), "appsettings.json");
        if (File.Exists(doUsuario)) return doUsuario;

        // Nada existe ainda: devolve o do sistema, que e onde o .deb coloca. A
        // mensagem de "nao encontrei" fica melhor apontando para la do que para
        // dentro de /opt.
        return doSistema;
    }

    public static string PastaDeConfiguracaoDoUsuario()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(config, "BuildMaker");
    }
}
