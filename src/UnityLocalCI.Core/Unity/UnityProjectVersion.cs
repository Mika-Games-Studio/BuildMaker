namespace UnityLocalCI.Core.Unity;

/// <summary>
/// Le a versao do editor gravada dentro do projeto Unity.
///
/// Todo projeto Unity carrega <c>ProjectSettings/ProjectVersion.txt</c>, escrito
/// pelo proprio editor e versionado junto com o projeto. E a unica fonte que nao
/// depende de alguem lembrar de atualizar a configuracao: quando o time sobe o
/// projeto para uma versao nova do Unity, este arquivo vem no commit.
///
/// O formato e YAML de uma linha so:
///
///     m_EditorVersion: 2022.3.62f3
///     m_EditorVersionWithRevision: 2022.3.62f3 (1a2b3c4d5e6f)
///
/// A segunda linha e ignorada de proposito: a revisao entre parenteses nao entra
/// no que o Unity CLI espera receber.
/// </summary>
public static class UnityProjectVersion
{
    public const string RelativePath = @"ProjectSettings\ProjectVersion.txt";

    private const string VersionKey = "m_EditorVersion:";

    /// <summary>
    /// Descobre a versao a partir de um caminho, que pode ser a raiz do projeto,
    /// a pasta ProjectSettings ou o proprio ProjectVersion.txt — as tres coisas
    /// que alguem pode acabar escolhendo numa caixa de selecao de pasta.
    ///
    /// Devolve nulo quando nao ha projeto Unity ali. Nao lanca: descobrir a
    /// versao e uma conveniencia, e falhar nisso nao pode impedir ninguem de
    /// terminar de preencher a configuracao.
    /// </summary>
    public static string? Read(string? path)
    {
        var file = Locate(path);
        if (file is null) return null;

        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(VersionKey, StringComparison.Ordinal)) continue;

                var version = trimmed[VersionKey.Length..].Trim();
                return version.Length == 0 ? null : version;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Onde esta o ProjectVersion.txt, se estiver.</summary>
    public static string? Locate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            if (File.Exists(path) &&
                Path.GetFileName(path).Equals("ProjectVersion.txt", StringComparison.OrdinalIgnoreCase))
                return path;

            if (!Directory.Exists(path)) return null;

            var direto = Path.Combine(path, RelativePath);
            if (File.Exists(direto)) return direto;

            // Alguem pode ter escolhido a propria pasta ProjectSettings.
            var dentro = Path.Combine(path, "ProjectVersion.txt");
            return File.Exists(dentro) ? dentro : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
