namespace UnityLocalCI.Core.Secrets;

/// <summary>
/// Leitura de segredos. O servico so le; quem grava e tools/set-secrets.ps1.
/// A configuracao guarda apenas o nome da credencial, nunca o valor.
/// </summary>
public interface ICredentialStore
{
    bool Exists(string credentialName);

    /// <summary>Retorna o segredo ou nulo se a credencial nao existir.</summary>
    string? Read(string credentialName);
}
