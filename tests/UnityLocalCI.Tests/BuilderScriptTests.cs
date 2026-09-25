using System.Text;
using UnityLocalCI.Core.Unity;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O Builder.cs entregue dentro do projeto Unity.
///
/// O que se protege aqui e o contrato com o '-executeMethod Builder.PerformBuild':
/// se o script deixar de existir no binario, ou deixar de declarar a classe com
/// esse nome exato, toda build sobe o editor, importa o projeto inteiro e so
/// entao morre com "executeMethod class 'Builder' could not be found" — um
/// minuto perdido para dizer que faltava um arquivo.
/// </summary>
public class BuilderScriptTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "ulci-builder-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CaminhoEsperado
        => Path.Combine(_workspace, BuilderScript.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void O_script_viaja_dentro_do_binario()
    {
        var conteudo = BuilderScript.Read();

        Assert.False(string.IsNullOrWhiteSpace(conteudo));
    }

    /// <summary>
    /// Sem namespace e com esse nome: e assim que o Unity resolve
    /// 'Builder.PerformBuild'. Um namespace adicionado no Builder.cs quebraria
    /// todas as builds, e o sintoma so apareceria depois da importacao.
    /// </summary>
    [Fact]
    public void O_script_declara_a_classe_que_o_executeMethod_procura()
    {
        var conteudo = BuilderScript.Read();

        Assert.Contains("public static class Builder", conteudo);
        Assert.Contains("public static void PerformBuild()", conteudo);

        // Declaracao, nao a palavra: ela aparece no comentario do proprio script.
        Assert.DoesNotContain(
            conteudo.Split('\n'),
            linha => linha.TrimStart().StartsWith("namespace ", StringComparison.Ordinal));
    }

    /// <summary>
    /// O #if UNITY_EDITOR e o que garante que o script nunca entre no player,
    /// mesmo que alguem o mova para fora de uma pasta Editor/.
    /// </summary>
    [Fact]
    public void O_script_e_so_de_editor()
        => Assert.StartsWith("#if UNITY_EDITOR", BuilderScript.Read());

    [Fact]
    public void O_script_e_escrito_sob_Assets_Editor()
    {
        var escreveu = BuilderScript.Deploy(_workspace);

        Assert.True(escreveu);
        Assert.True(File.Exists(CaminhoEsperado));
        Assert.Equal(BuilderScript.Read(), File.ReadAllText(CaminhoEsperado));
    }

    /// <summary>
    /// Reescrever um arquivo identico muda a data de modificacao, e o Unity
    /// recompila os assemblies de editor por causa disso — vinte e poucos
    /// segundos em toda build, sem nenhum ganho.
    /// </summary>
    [Fact]
    public void Entregar_de_novo_nao_toca_no_arquivo_que_ja_esta_certo()
    {
        BuilderScript.Deploy(_workspace);
        var antes = File.GetLastWriteTimeUtc(CaminhoEsperado);

        var escreveu = BuilderScript.Deploy(_workspace);

        Assert.False(escreveu);
        Assert.Equal(antes, File.GetLastWriteTimeUtc(CaminhoEsperado));
    }

    [Fact]
    public void Um_script_desatualizado_e_substituido()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CaminhoEsperado)!);
        File.WriteAllText(CaminhoEsperado, "// versao antiga");

        var escreveu = BuilderScript.Deploy(_workspace);

        Assert.True(escreveu);
        Assert.Equal(BuilderScript.Read(), File.ReadAllText(CaminhoEsperado));
    }

    /// <summary>
    /// Um BOM no inicio do arquivo aparece como caractere invisivel antes do
    /// '#if' em alguns editores, e confunde quem for ler o script no projeto.
    /// </summary>
    [Fact]
    public void O_arquivo_escrito_nao_tem_BOM()
    {
        BuilderScript.Deploy(_workspace);

        var primeiros = File.ReadAllBytes(CaminhoEsperado).Take(3).ToArray();
        Assert.NotEqual(Encoding.UTF8.GetPreamble(), primeiros);
    }
}
