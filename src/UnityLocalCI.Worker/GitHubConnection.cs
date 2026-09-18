using System.Diagnostics;

namespace UnityLocalCI.App;

/// <summary>Nomes e regras da conexao com o GitHub feita pela janela.</summary>
internal static class GitHubConnection
{
    /// <summary>
    /// Credencial unica da maquina. Um nome fixo para quem conecta nao precisar
    /// inventar nem decorar nada — os projetos apontam todos para ela.
    /// </summary>
    public const string CredentialName = "UnityLocalCI_GitHub";

    /// <summary>
    /// Client ID embutido no programa, como o GitHub Desktop faz.
    ///
    /// O GitHub Desktop nao pede Client ID a ninguem porque ele e um OAuth App
    /// registrado e carrega o proprio id aqui dentro — o id e publico, o fluxo
    /// de dispositivo nao usa client secret. Para ficar igual, basta registrar
    /// um OAuth App uma vez (github.com/settings/applications/new, com
    /// 'Enable Device Flow' marcado) e colar o id nesta linha: a partir dai
    /// ninguem mais ve este campo.
    ///
    /// Usar o id de outro programa — o do proprio GitHub Desktop, o do gh —
    /// seria se passar por ele para o servidor e para quem autoriza. Entao esta
    /// constante nasce vazia, e o Client ID continua podendo vir da
    /// configuracao enquanto nao houver um App registrado.
    /// </summary>
    public const string BuiltInClientId = "";

    /// <summary>
    /// O Client ID que vale: o da configuracao ganha do embutido, para uma
    /// maquina poder apontar para outro OAuth App sem recompilar.
    /// </summary>
    public static string? ClientIdEmVigor(string? daConfiguracao)
    {
        if (!string.IsNullOrWhiteSpace(daConfiguracao)) return daConfiguracao.Trim();

        return string.IsNullOrWhiteSpace(BuiltInClientId) ? null : BuiltInClientId.Trim();
    }

    public static bool IsGitHub(string? url)
        => url is not null && url.Contains("github.com", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A sessao do GitHub CLI, quando ele existe nesta maquina.
///
/// E o caminho de zero configuracao: quem ja usa o 'gh' no dia a dia ja
/// autorizou o GitHub uma vez, e nao precisa de OAuth App nenhum. O token e
/// lido e entregue ao cofre; nada e escrito em log nem mostrado na tela.
/// </summary>
internal static class GitHubCli
{
    private static bool? _disponivel;

    public static bool Available => _disponivel ??= Rodar("--version") is not null;

    /// <summary>Token da sessao atual, ou nulo se o gh nao existe ou nao esta conectado.</summary>
    public static string? ReadToken()
    {
        var saida = Rodar("auth token");
        if (saida is null) return null;

        var token = saida.Trim();
        return token.Length == 0 ? null : token;
    }

    private static string? Rodar(string argumentos)
    {
        try
        {
            using var processo = Process.Start(new ProcessStartInfo("gh", argumentos)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (processo is null) return null;

            var saida = processo.StandardOutput.ReadToEnd();

            // Prazo curto: isto e chamado de um clique na janela, e um 'gh'
            // esperando algo no terminal nao pode prender a tela.
            if (!processo.WaitForExit(10_000))
            {
                try { processo.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            return processo.ExitCode == 0 ? saida : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // gh nao instalado.
            return null;
        }
    }
}
