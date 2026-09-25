using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Unity;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O limite de 260 caracteres do Windows, dito em voz alta.
///
/// Estes testes existem por uma build que falhou aqui com 91 erros e nenhuma
/// palavra sobre o motivo: o workspace tinha 181 caracteres e um arquivo de
/// pacote do Unity chegou a 261. O Unity despeja 'DirectoryNotFoundException',
/// 'Host type is not matching any asset type' e 'TypeDB: Assembly index ... was
/// not found' — e quem le vai procurar defeito no projeto.
/// </summary>
public class LongPathDiagnosticTests
{
    private static string CaminhoCom(int tamanho)
    {
        const string raiz = @"C:\ci\";
        return raiz + new string('a', Math.Max(0, tamanho - raiz.Length));
    }

    [Fact]
    public void Erro_com_caminho_de_261_caracteres_ganha_a_dica()
    {
        var log = $"DirectoryNotFoundException: Could not find a part of the path \"{CaminhoCom(261)}\"";

        var resumo = UnityLogParser.ParseText(
            "[UnityLocalCI] ERRO: build terminou como Failed apos 24s com 91 erro(s).\n" + log);

        Assert.StartsWith(UnityLogParser.LongPathHint, resumo.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sem caminho longo, nada de dica: um palpite errado no topo do resumo
    /// manda a pessoa para o lado oposto do problema.
    /// </summary>
    [Fact]
    public void Erro_comum_nao_ganha_a_dica()
    {
        var resumo = UnityLogParser.ParseText(
            "Assets/Scripts/Player.cs(12,9): error CS0103: The name 'foo' does not exist");

        Assert.NotNull(resumo.Summary);
        Assert.DoesNotContain("260+", resumo.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public void Caminho_curto_dentro_de_linha_longa_nao_dispara()
    {
        var log = @"Error: algo deu errado em C:\ci\workspace\Jogo\Assets\Scripts\Player.cs " + new string('x', 400);

        var resumo = UnityLogParser.ParseText(log);

        Assert.NotNull(resumo.Summary);
        Assert.DoesNotContain("260+", resumo.Summary!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\ci\workspace\CrashUnity", false)]
    [InlineData(@"C:\ci\workspace", false)]
    public void Workspace_curto_nao_e_arriscado(string caminho, bool arriscado)
        => Assert.Equal(arriscado, WorkspacePathLimit.Arriscado(caminho));

    [Fact]
    public void Workspace_longo_e_arriscado_e_a_explicacao_diz_o_tamanho()
    {
        var caminho = CaminhoCom(181);

        Assert.True(WorkspacePathLimit.Arriscado(caminho));
        Assert.Contains("181", WorkspacePathLimit.Explicacao("SmokeCI", caminho), StringComparison.Ordinal);
    }

    [Fact]
    public void Sem_workspace_nao_ha_o_que_avisar()
    {
        Assert.False(WorkspacePathLimit.Arriscado(null));
        Assert.False(WorkspacePathLimit.Arriscado("   "));
    }
}
