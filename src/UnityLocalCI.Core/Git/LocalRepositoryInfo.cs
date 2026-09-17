namespace UnityLocalCI.Core.Git;

/// <summary>O que um clone local sabe dizer sobre si mesmo.</summary>
public sealed record LocalRepository(string Root, string? Url, string? Branch);

/// <summary>
/// Le o remote e a branch de um clone existente, sem invocar o git.
///
/// Serve para vincular um projeto pela janela: o usuario aponta a pasta do
/// projeto Unity que ja tem na maquina, e a configuracao se preenche com a URL
/// e a branch que ele ja usa, em vez de ele digitar tudo de novo e errar.
///
/// Lendo os arquivos e nao chamando o git porque isto roda na thread da
/// interface, ao lado de uma caixa de dialogo: um processo externo ali travaria
/// a janela, e o unico ganho seria cobrir configuracoes que esta tela nem
/// oferece.
/// </summary>
public static class LocalRepositoryInfo
{
    /// <summary>
    /// Sobe a partir da pasta escolhida ate achar um repositorio. Devolve nulo
    /// se nao houver nenhum — a pasta pode ser so um projeto Unity solto.
    /// </summary>
    public static LocalRepository? Read(string? folder)
    {
        var git = LocateGitFolder(folder);
        if (git is null) return null;

        return new LocalRepository(
            Root: Path.GetDirectoryName(git.TrimEnd(Path.DirectorySeparatorChar)) ?? git,
            Url: ReadOriginUrl(Path.Combine(git, "config")),
            Branch: ReadBranch(Path.Combine(git, "HEAD")));
    }

    /// <summary>
    /// Branches que o clone local ja conhece, lidas dos refs no disco.
    ///
    /// Serve de resposta imediata enquanto a consulta ao servidor nao volta, e
    /// de unica resposta quando nao ha rede ou credencial. Le tanto os refs
    /// soltos quanto o packed-refs, porque o git compacta os dois formatos sem
    /// avisar.
    /// </summary>
    public static IReadOnlyList<string> ReadKnownBranches(string? folder)
    {
        var git = LocateGitFolder(folder);
        if (git is null) return [];

        var branches = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var remotos = Path.Combine(git, "refs", "remotes", "origin");
            if (Directory.Exists(remotos))
            {
                foreach (var arquivo in Directory.EnumerateFiles(remotos, "*", SearchOption.AllDirectories))
                {
                    var nome = Path.GetRelativePath(remotos, arquivo).Replace('\\', '/');

                    // origin/HEAD e um apelido para a branch padrao, nao uma branch.
                    if (nome is not "HEAD") branches.Add(nome);
                }
            }

            var packed = Path.Combine(git, "packed-refs");
            if (File.Exists(packed))
            {
                foreach (var linha in File.ReadLines(packed))
                {
                    const string prefixo = "refs/remotes/origin/";
                    var indice = linha.IndexOf(prefixo, StringComparison.Ordinal);
                    if (indice < 0) continue;

                    var nome = linha[(indice + prefixo.Length)..].Trim();
                    if (nome.Length > 0 && nome != "HEAD") branches.Add(nome);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return branches.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? LocateGitFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;

        try
        {
            var atual = new DirectoryInfo(folder);

            while (atual is not null)
            {
                var caminho = Path.Combine(atual.FullName, ".git");

                if (Directory.Exists(caminho)) return caminho;

                // Worktree ou submodulo: o .git e um arquivo apontando para a
                // pasta de verdade.
                if (File.Exists(caminho))
                {
                    var conteudo = File.ReadAllText(caminho).Trim();
                    const string prefixo = "gitdir:";

                    if (!conteudo.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)) return null;

                    var destino = conteudo[prefixo.Length..].Trim();
                    if (!Path.IsPathRooted(destino)) destino = Path.GetFullPath(Path.Combine(atual.FullName, destino));

                    return Directory.Exists(destino) ? destino : null;
                }

                atual = atual.Parent;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// O remote chamado origin; na falta dele, o primeiro que aparecer. Um clone
    /// com o remote renomeado ainda e util para preencher a tela.
    /// </summary>
    private static string? ReadOriginUrl(string configPath)
    {
        if (!File.Exists(configPath)) return null;

        try
        {
            string? secaoAtual = null;
            string? primeiro = null;

            foreach (var linha in File.ReadLines(configPath))
            {
                var texto = linha.Trim();

                if (texto.StartsWith('[') && texto.EndsWith(']'))
                {
                    secaoAtual = texto[1..^1].Trim();
                    continue;
                }

                if (secaoAtual is null || !secaoAtual.StartsWith("remote ", StringComparison.OrdinalIgnoreCase)) continue;

                var igual = texto.IndexOf('=');
                if (igual < 0) continue;

                if (!texto[..igual].Trim().Equals("url", StringComparison.OrdinalIgnoreCase)) continue;

                var url = texto[(igual + 1)..].Trim();
                if (url.Length == 0) continue;

                if (secaoAtual.Contains("\"origin\"", StringComparison.OrdinalIgnoreCase)) return url;

                primeiro ??= url;
            }

            return primeiro;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Nulo em HEAD solto: nao ha branch para observar.</summary>
    private static string? ReadBranch(string headPath)
    {
        if (!File.Exists(headPath)) return null;

        try
        {
            var conteudo = File.ReadAllText(headPath).Trim();
            const string prefixo = "ref: refs/heads/";

            return conteudo.StartsWith(prefixo, StringComparison.Ordinal)
                ? conteudo[prefixo.Length..].Trim()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
