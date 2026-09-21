using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Unity;

namespace UnityLocalCI.App;

/// <summary>
/// Monta o cadastro de um projeto a partir de uma pasta que ja existe na
/// maquina: dela saem a URL do remote, a branch atual e a versao do editor.
///
/// Fica separado da janela, e sem caixa de dialogo nenhuma, para poder ser
/// testado: o que erra aqui — nome repetido, workspace apontado para o lugar
/// errado — so apareceria na primeira build.
/// </summary>
public static class ProjectDraft
{
    public const string DefaultWorkspaceRoot = @"C:\ci\workspace";
    public const string DefaultTriggerRoot = @"C:\ci\triggers";

    /// <summary>Alvo do build de quem nao escolher outro.</summary>
    public const string DefaultBuildTarget = "WebGL";

    /// <summary>O que foi possivel descobrir sobre a pasta escolhida.</summary>
    public sealed record Result(ProjectOptions Project, string? EditorVersion, LocalRepository? Repository)
    {
        public bool Recognized => EditorVersion is not null || Repository is not null;
    }

    public static Result FromFolder(string folder, IReadOnlyList<ProjectOptions> existing)
    {
        // Com prazo: a pasta escolhida pode estar num compartilhamento de rede,
        // e isto e chamado da thread da janela, logo depois da caixa de dialogo.
        var versao = BoundedIo.Run(() => UnityProjectVersion.Read(folder));
        var repositorio = BoundedIo.Run(() => LocalRepositoryInfo.Read(folder));

        var nome = SuggestName(repositorio?.Root ?? folder, existing);

        var projeto = new ProjectOptions
        {
            Name = nome,

            // Desligado: quem cadastrou ainda precisa escolher a pasta de
            // destino, e um projeto que comeca a construir sozinho antes disso
            // so produziria falha.
            Enabled = false,

            Repository = new RepositoryOptions
            {
                Url = repositorio?.Url ?? "",
                Branch = repositorio?.Branch ?? "HML",

                // Workspace proprio do CI, nunca a pasta escolhida: antes de
                // cada build o pipeline apaga o que nao esta commitado, e isso
                // na pasta de trabalho de alguem seria destrutivo.
                WorkspacePath = Path.Combine(RootOf(existing.Select(p => p.Repository?.WorkspacePath), DefaultWorkspaceRoot), nome),

                // Quase sempre e a mesma credencial do projeto ja cadastrado.
                PatCredentialName = existing
                    .Select(p => p.Repository?.PatCredentialName)
                    .FirstOrDefault(credencial => !string.IsNullOrWhiteSpace(credencial)),
            },

            ManualTriggerFile = Path.Combine(RootOf(existing.Select(p => p.ManualTriggerFile), DefaultTriggerRoot), nome + ".txt"),

            // WebGL explicito: e o alvo deste time, e ver a plataforma escrita
            // no cadastro evita a duvida de "sera que herdou o que eu acho?".
            Unity = new UnityOptions { EditorVersion = versao, BuildTarget = DefaultBuildTarget },
            Publishing = new PublishingOptions(),
        };

        return new Result(projeto, versao, repositorio);
    }

    /// <summary>Nome derivado da pasta, sem caractere estranho e sem repetir um ja cadastrado.</summary>
    public static string SuggestName(string folder, IReadOnlyList<ProjectOptions> existing)
    {
        var bruto = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var baseNome = new string((bruto ?? "")
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());

        if (baseNome.Length == 0) baseNome = "Projeto";

        var nome = baseNome;
        var sufixo = 2;

        while (existing.Any(p => string.Equals(p.Name, nome, StringComparison.OrdinalIgnoreCase)))
            nome = baseNome + sufixo++;

        return nome;
    }

    /// <summary>
    /// Segue a pasta que os projetos ja cadastrados usam, em vez de impor a
    /// padrao: numa maquina onde tudo esta em D:, um caminho novo em C: passaria
    /// despercebido ate faltar espaco.
    /// </summary>
    private static string RootOf(IEnumerable<string?> caminhos, string padrao)
    {
        var existente = caminhos.FirstOrDefault(caminho => !string.IsNullOrWhiteSpace(caminho));
        if (existente is null) return padrao;

        var pai = Path.GetDirectoryName(existente);
        return string.IsNullOrWhiteSpace(pai) ? padrao : pai;
    }
}
