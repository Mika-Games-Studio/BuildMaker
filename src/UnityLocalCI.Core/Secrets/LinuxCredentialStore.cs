using System.Text;

namespace UnityLocalCI.Core.Secrets;

/// <summary>
/// Arquivo por credencial, com permissao 0600, sob o diretorio de configuracao
/// do usuario.
///
/// POR QUE NAO E UM COFRE DE VERDADE
///
/// Isto e mais fraco que o <see cref="WindowsCredentialStore"/>, e vale dizer em
/// voz alta: no Windows o segredo e cifrado pelo sistema com a chave da conta, e
/// aqui ele fica em texto claro num arquivo. Quem ler o arquivo le o segredo.
///
/// A alternativa no Linux seria o Secret Service (libsecret, gnome-keyring), que
/// cifra de verdade — mas ele e um servico de sessao grafica: precisa de um
/// D-Bus de sessao e de uma carteira destrancada por alguem que digitou uma
/// senha. Num servidor Ubuntu sem interface, que e onde este modo roda, esse
/// servico nao existe, e o pipeline travaria esperando uma carteira que ninguem
/// vai abrir.
///
/// A protecao real aqui e a do sistema de arquivos: 0600 no arquivo e 0700 no
/// diretorio, entao so o dono le. E o mesmo acordo que git-credential-store,
/// docker, kubectl e aws-cli fazem no Linux — nao porque errado seja certo, mas
/// porque num servidor sem sessao grafica esta e a fronteira que existe.
///
/// Continua valendo a regra do projeto: a configuracao guarda apenas o NOME da
/// credencial. O valor nunca aparece em log, mensagem de erro ou URL de remote.
///
/// O nome diz onde ele e USADO, e nao onde ele roda: nao ha nada aqui que exija
/// Linux — e um arquivo e uma permissao — e e por isso que ele nao leva
/// UnsupportedOSPlatform. A suite de testes roda no Windows, e uma classe que
/// so pudesse ser exercitada numa maquina Linux nao seria testada nunca.
/// </summary>
public sealed class LinuxCredentialStore : ICredentialStore
{
    private readonly string _pasta;

    public LinuxCredentialStore() : this(PastaPadrao()) { }

    public LinuxCredentialStore(string pasta) => _pasta = pasta;

    /// <summary>
    /// $XDG_CONFIG_HOME/BuildMaker/credenciais, ou ~/.config/... quando a
    /// variavel nao esta definida — que e o que a especificacao do XDG manda.
    /// </summary>
    private static string PastaPadrao()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(config, "BuildMaker", "credenciais");
    }

    public bool Exists(string credentialName) => Read(credentialName) is not null;

    public string? Read(string credentialName)
    {
        if (string.IsNullOrWhiteSpace(credentialName)) return null;

        var caminho = CaminhoDe(credentialName);
        if (!File.Exists(caminho)) return null;

        try
        {
            // Sem a quebra de linha final: um editor de texto costuma acrescentar
            // uma, e um PAT com '\n' no fim vira um cabecalho HTTP invalido — erro
            // que aparece como 401 e custa a ser entendido.
            return File.ReadAllText(caminho, Encoding.UTF8).TrimEnd('\r', '\n');
        }
        catch (UnauthorizedAccessException excecao)
        {
            // Sem o conteudo na mensagem: o caminho pode ir para o log, o valor nao.
            throw new InvalidOperationException(
                $"Sem permissao para ler a credencial '{credentialName}' em {caminho}.", excecao);
        }
    }

    public void Write(string credentialName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialName);
        ArgumentNullException.ThrowIfNull(secret);

        Directory.CreateDirectory(_pasta);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_pasta, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var caminho = CaminhoDe(credentialName);

        // O arquivo nasce com 0600 ANTES de receber o segredo. Escrever primeiro
        // e ajustar a permissao depois deixaria uma janela — curta, mas real — em
        // que o segredo esta no disco legivel por qualquer usuario da maquina.
        if (!File.Exists(caminho))
        {
            using (File.Create(caminho)) { }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(caminho, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.WriteAllText(caminho, secret, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// O nome vem da configuracao, entao nao pode virar caminho: 'x/../../y'
    /// escreveria fora da pasta. Tudo que nao for letra, numero, '-', '_' ou '.'
    /// e trocado.
    /// </summary>
    private string CaminhoDe(string credentialName)
    {
        var limpo = string.Concat(credentialName.Select(
            c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

        // '.' e '..' sobreviveriam ao filtro acima e continuariam sendo diretorios.
        if (limpo is "." or "..") limpo = "_" + limpo;

        return Path.Combine(_pasta, limpo);
    }
}
