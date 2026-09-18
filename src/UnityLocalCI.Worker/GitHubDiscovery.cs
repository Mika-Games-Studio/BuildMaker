using UnityLocalCI.Core.Secrets;

namespace UnityLocalCI.App;

/// <summary>Uma conta do GitHub que ja existe nesta maquina.</summary>
/// <param name="Origem">De onde ela veio, em portugues, para aparecer na tela.</param>
/// <param name="Token">O segredo. Nao vai para log, arquivo nem tela.</param>
/// <param name="JaNoCofre">Verdadeiro quando ela ja estava guardada com o nosso nome.</param>
internal sealed record ContaEncontrada(string Origem, string Token, bool JaNoCofre);

/// <summary>
/// Procura uma conta do GitHub que esta maquina ja tenha.
///
/// Esta e a primeira pergunta a fazer, antes de mandar alguem para o navegador:
/// quem ja clonou por HTTPS aqui, ja usa o GitHub Desktop ou ja rodou
/// 'gh auth login' tem um token guardado, e pedir outro e pedir o que ja se tem.
///
/// A ordem e do mais especifico para o mais geral:
///
///   1. o nosso proprio nome no cofre, de uma conexao anterior;
///   2. a conta que o Git desta maquina usa — a mesma que o GitHub Desktop grava;
///   3. a sessao do GitHub CLI.
///
/// So quando nenhuma responde e que o caminho do navegador faz sentido.
/// </summary>
internal sealed class GitHubDiscovery(
    ICredentialStore cofre,
    Func<string?>? contaDoGit = null,
    Func<string?>? sessaoDoGh = null)
{
    private readonly Func<string?> _contaDoGit = contaDoGit ?? (() => GitCredentials.ReadToken());
    private readonly Func<string?> _sessaoDoGh = sessaoDoGh ?? GitHubCli.ReadToken;

    /// <summary>A primeira conta encontrada, ou nulo quando a maquina nao tem nenhuma.</summary>
    public ContaEncontrada? Procurar()
    {
        if (Limpo(DoCofre()) is { } guardado)
            return new ContaEncontrada("o acesso que já estava no cofre do Windows", guardado, JaNoCofre: true);

        if (Limpo(Tentar(_contaDoGit)) is { } doGit)
            return new ContaEncontrada("a conta do Git desta máquina", doGit, JaNoCofre: false);

        if (Limpo(Tentar(_sessaoDoGh)) is { } doGh)
            return new ContaEncontrada("a sessão do GitHub CLI", doGh, JaNoCofre: false);

        return null;
    }

    /// <summary>
    /// Deixa a conta no cofre com o nome fixo da maquina. Quem ja estava la fica
    /// como esta: reescrever o mesmo segredo so serviria para arriscar perde-lo.
    /// </summary>
    public void Guardar(ContaEncontrada conta)
    {
        if (conta.JaNoCofre) return;

        cofre.Write(GitHubConnection.CredentialName, conta.Token);
    }

    private string? DoCofre()
    {
        try { return cofre.Read(GitHubConnection.CredentialName); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Um caminho que falha nao pode derrubar os outros: o 'git' pode nao estar
    /// no PATH, o auxiliar de credencial pode estar quebrado, e mesmo assim o
    /// 'gh' ao lado pode responder.
    /// </summary>
    private static string? Tentar(Func<string?> caminho)
    {
        try { return caminho(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
    }

    private static string? Limpo(string? valor)
        => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}
