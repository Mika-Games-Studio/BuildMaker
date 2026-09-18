namespace UnityLocalCI.Core.Secrets;

/// <summary>
/// Leitura e gravacao de segredos no cofre do Windows. A configuracao guarda
/// apenas o NOME da credencial, nunca o valor.
///
/// O pipeline so le. Gravar existe por causa da conexao com o GitHub feita pela
/// janela: o token chega pelo navegador e vai direto para o cofre, sem passar
/// por arquivo, area de transferencia nem pela tela.
/// </summary>
public interface ICredentialStore
{
    bool Exists(string credentialName);

    /// <summary>Retorna o segredo ou nulo se a credencial nao existir.</summary>
    string? Read(string credentialName);

    /// <summary>Grava (ou substitui) o segredo, com escopo da conta do Windows atual.</summary>
    void Write(string credentialName, string secret);
}
