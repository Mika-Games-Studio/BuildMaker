using System.Reflection;
using System.Text;

namespace UnityLocalCI.Core.Unity;

/// <summary>
/// O Builder.cs, embutido no binario e escrito dentro do projeto Unity.
///
/// O pipeline chama o Unity com '-executeMethod Builder.PerformBuild'. Esse
/// metodo so existe se o projeto tiver o script; sem ele o editor sobe, importa
/// tudo e morre com "executeMethod class 'Builder' could not be found".
///
/// O script poderia ser versionado dentro de cada jogo, e era assim que comecou.
/// Nao vale a pena: cada projeto novo passa a exigir um passo manual, e — pior —
/// copias antigas continuam vivas depois que o pipeline muda o contrato entre os
/// dois (os prefixos que <see cref="UnityLogParser"/> procura, os argumentos -ci*
/// que o <see cref="UnityCliClient"/> passa). A copia e o pipeline saem de sincronia
/// em silencio, e a build falha por um motivo que ninguem relaciona com a causa.
///
/// Entao o arquivo viaja junto do binario e e escrito a cada sincronizacao. O CI
/// e dono dele; o repositorio do jogo nao precisa saber que o CI existe.
/// </summary>
public static class BuilderScript
{
    /// <summary>
    /// Pasta, relativa a raiz do projeto, onde o script e escrito.
    ///
    /// Fica num diretorio proprio para deixar obvio de onde veio, e sob
    /// Assets/Editor/ porque e la que o Unity compila codigo de editor — o
    /// arquivo nunca entra no player.
    /// </summary>
    public const string RelativeFolder = "Assets/Editor/UnityLocalCI";

    /// <summary>Caminho do script, relativo a raiz do projeto.</summary>
    public const string RelativePath = RelativeFolder + "/Builder.cs";

    /// <summary>Nome logico do recurso embutido, fixado no csproj do Core.</summary>
    internal const string ResourceName = "UnityLocalCI.Builder.cs";

    /// <summary>O conteudo do script, como ele foi compilado dentro do binario.</summary>
    public static string Read()
    {
        using var stream = typeof(BuilderScript).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"O recurso embutido '{ResourceName}' nao esta no binario. " +
                "Confira o EmbeddedResource em UnityLocalCI.Core.csproj.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Garante que o projeto em <paramref name="workspacePath"/> tem o script.
    /// </summary>
    /// <returns>
    /// true quando o arquivo foi criado ou atualizado; false quando ja estava
    /// igual. A distincao importa: reescrever um arquivo identico muda a data de
    /// modificacao, e o Unity responde a isso recompilando os assemblies de
    /// editor — vinte e poucos segundos por build, a toa.
    /// </returns>
    public static bool Deploy(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var destino = Path.Combine(workspacePath, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var conteudo = Read();

        if (File.Exists(destino) && File.ReadAllText(destino, Encoding.UTF8) == conteudo)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

        // Sem BOM: o compilador do Unity le UTF-8 puro, e um BOM aparece como
        // caractere invisivel no inicio do arquivo em alguns editores.
        File.WriteAllText(destino, conteudo, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }
}
