namespace UnityLocalCI.Core;

/// <summary>
/// Onde o BuildMaker guarda o que e dele.
///
/// Segue o padrao do Windows: dados de um aplicativo por usuario vivem em
/// %LOCALAPPDATA%\&lt;Produto&gt;. Local, e nao Roaming, porque nada disto faz
/// sentido em outra maquina — o banco aponta para workspaces, caminhos de disco
/// e uma instalacao do Unity desta maquina especifica; carregar isso num perfil
/// que acompanha o usuario so serviria para descrever um mundo que nao existe do
/// outro lado.
///
/// Antes cada coisa tinha seu lugar improvisado: o banco e os logs em C:\ci, e o
/// status e o historico misturados na pasta de destino das builds, no meio dos
/// zips. A pasta de builds agora e so dos zips.
///
/// A pasta de trabalho dos projetos (workspace, staging, gatilhos) continua fora
/// daqui, e deve continuar: sao dezenas de gigabytes por projeto, e a raiz curta
/// existe porque o Unity e o IL2CPP esbarram no limite de caminho do Windows.
/// </summary>
public static class AppPaths
{
    /// <summary>Nome do produto, como aparece para quem abre a pasta.</summary>
    public const string ProductFolder = "BuildMaker";

    /// <summary>%LOCALAPPDATA%\BuildMaker.</summary>
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductFolder);

    /// <summary>Onde o status e o historico em JSON sao escritos.</summary>
    public static string StatusFolder => Path.Combine(DataRoot, "status");

    /// <summary>Banco de estado, quando a configuracao nao diz outro lugar.</summary>
    public static string DefaultDatabasePath => Path.Combine(DataRoot, "state", "buildmaker.db");

    /// <summary>
    /// Log do servico, quando a configuracao nao diz outro lugar. Guarda so o
    /// log do proprio programa: o log de cada build nao vai mais para disco.
    /// </summary>
    public static string DefaultLogFolder => Path.Combine(DataRoot, "logs");

    /// <summary>Arquivo consolidado de todos os projetos.</summary>
    public static string DefaultGlobalStatusFile => Path.Combine(StatusFolder, "geral.json");

    /// <summary>O status de um projeto. O nome e higienizado: ele vem da configuracao.</summary>
    public static string ProjectStatusFile(string project)
        => Path.Combine(StatusFolder, SanitizeFileName(project) + ".json");

    private static string SanitizeFileName(string value)
        => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
