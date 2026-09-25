using UnityLocalCI.Core.Unity;

namespace UnityLocalCI.Core.Git;

/// <summary>
/// Os argumentos do 'git clean' do passo de sincronizacao.
///
/// CRITICO: as exclusoes preservam o cache do Unity. Sem elas, 'git clean -xdf'
/// apaga Library/ e toda build vira build limpa, saindo de ~15 para ~45 minutos.
/// Esta classe existe separada justamente para que um teste de regressao possa
/// fixar a lista e falhar se alguem a encurtar.
/// </summary>
public static class GitCleanArguments
{
    /// <summary>Diretorios que o clean nunca pode remover.</summary>
    public static readonly IReadOnlyList<string> PreservedPaths = new[]
    {
        "Library/",
        "Temp/",
        "obj/",
        "Logs/",
        "UserSettings/",
    };

    /// <summary>
    /// Caminhos que o CI escreve dentro do projeto e que tambem sobrevivem ao clean.
    ///
    /// Nao sao cache: sao arquivos nossos. Se o clean os apagasse, o Sync os
    /// reescreveria logo em seguida com data nova, e o Unity recompilaria os
    /// assemblies de editor a cada build por causa disso. Ver <see cref="BuilderScript"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> CiOwnedPaths = new[]
    {
        BuilderScript.RelativeFolder + "/",
    };

    /// <summary>Monta 'clean -xdf -e Library/ -e Temp/ ...'.</summary>
    public static IReadOnlyList<string> Build()
    {
        var args = new List<string> { "clean", "-xdf" };
        foreach (var path in PreservedPaths.Concat(CiOwnedPaths))
        {
            args.Add("-e");
            args.Add(path);
        }
        return args;
    }
}
