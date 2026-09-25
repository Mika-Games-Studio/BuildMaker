namespace UnityLocalCI.Core.Configuration;

/// <summary>
/// O limite de 260 caracteres do Windows, visto do workspace.
///
/// Existe porque a falha e indecifravel quando acontece. O Unity nao diz
/// "caminho longo demais": ele despeja dezenas de
/// 'DirectoryNotFoundException' em arquivos de dentro de Library/PackageCache,
/// mais 'Host type is not matching any asset type' e 'TypeDB: Assembly index
/// ... was not found', e a build morre com noventa e tantos erros que nao
/// mencionam o motivo em lugar nenhum.
///
/// Aconteceu aqui: um workspace com 181 caracteres, e um arquivo de pacote com
/// 261. Quem ler o log sem saber disso vai procurar defeito no projeto Unity.
/// </summary>
public static class WorkspacePathLimit
{
    /// <summary>O teto do Windows para caminho sem prefixo estendido.</summary>
    public const int MaxPath = 260;

    /// <summary>
    /// Quanto os pacotes do Unity costumam somar abaixo do workspace. Nao e
    /// chute: 'Library/PackageCache/com.unity.render-pipelines.universal@<hash>/
    /// Editor/...' passa de 120 caracteres com folga em projetos comuns.
    /// </summary>
    public const int ReservaParaPacotes = 150;

    /// <summary>Acima disto, a build tende a falhar sem dizer por que.</summary>
    public const int Confortavel = MaxPath - ReservaParaPacotes;

    public static bool Arriscado(string? workspacePath)
        => !string.IsNullOrWhiteSpace(workspacePath) && workspacePath.Trim().Length > Confortavel;

    /// <summary>A explicacao, pronta para o log e para a tela.</summary>
    public static string Explicacao(string projeto, string workspacePath) =>
        $"[{projeto}] o workspace tem {workspacePath.Length} caracteres, e o Windows para em {MaxPath}. " +
        $"Os pacotes do Unity somam mais de {ReservaParaPacotes} abaixo dele, entao a build vai falhar com " +
        "dezenas de 'DirectoryNotFoundException' que nao mencionam o tamanho do caminho. " +
        @"Use algo curto, como C:\ci\workspace\<projeto>.";
}
