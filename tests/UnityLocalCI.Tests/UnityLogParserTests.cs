using UnityLocalCI.Core.Unity;
using Xunit;

namespace UnityLocalCI.Tests;

public class UnityLogParserTests
{
    [Fact]
    public void Extrai_erro_de_compilacao_do_meio_de_um_log_enorme()
    {
        var lines = new List<string>();
        for (var i = 0; i < 5000; i++) lines.Add($"Loading assembly {i}");
        lines.Add(@"Assets\Scripts\Player.cs(42,17): error CS0103: The name 'velocty' does not exist in the current context");
        for (var i = 0; i < 5000; i++) lines.Add($"Unloading {i} unused Assets");

        var summary = UnityLogParser.Parse(lines);

        var error = Assert.Single(summary.CompilationErrors);
        Assert.Contains("CS0103", error);
        Assert.NotNull(summary.Summary);
        Assert.Contains("CS0103", summary.Summary!);
    }

    [Fact]
    public void Log_de_build_bem_sucedida_nao_produz_resumo_de_erro()
    {
        var summary = UnityLogParser.ParseText("""
            Build completed with a result of 'Succeeded'
            Total build time: 00:12:40
            """);

        Assert.Empty(summary.CompilationErrors);
        Assert.Null(summary.Summary);
    }

    [Fact]
    public void Erros_repetidos_aparecem_uma_vez_so()
    {
        const string error = @"Assets\A.cs(1,1): error CS1002: ; expected";
        var summary = UnityLogParser.ParseText(string.Join('\n', Enumerable.Repeat(error, 50)));

        Assert.Single(summary.CompilationErrors);
    }

    [Fact]
    public void Falha_sem_erro_de_compilacao_cai_nos_erros_gerais()
    {
        var summary = UnityLogParser.ParseText("""
            Starting build
            BuildFailedException: Build failed with 1 error
            """);

        Assert.Empty(summary.CompilationErrors);
        Assert.NotNull(summary.Summary);
        Assert.Contains("BuildFailedException", summary.Summary!);
    }

    [Fact]
    public void Erro_do_proprio_cli_entra_no_resumo()
    {
        // Caso real observado: o CLI recusa uma pasta que nao e projeto Unity.
        var summary = UnityLogParser.ParseText(
            @"Error: Not a Unity project (ProjectVersion.txt not found at C:\ci\workspace\smoke).");

        Assert.NotNull(summary.Summary);
        Assert.Contains("Not a Unity project", summary.Summary!);
    }

    [Fact]
    public void Aviso_marcado_pelo_builder_e_capturado_para_o_status()
    {
        var summary = UnityLogParser.ParseText($"""
            Alguma linha qualquer
            {UnityLogParser.HighlightedWarningPrefix} compressao Brotli sem decompressionFallback
            Outra linha
            """);

        var warning = Assert.Single(summary.Warnings);
        Assert.Contains("Brotli", warning);
        Assert.Empty(summary.CompilationErrors);
    }

    [Fact]
    public void Warning_de_compilador_nao_vira_erro()
    {
        var summary = UnityLogParser.ParseText(
            @"Assets\A.cs(3,9): warning CS0168: The variable 'e' is declared but never used");

        Assert.Empty(summary.CompilationErrors);
        Assert.Single(summary.Warnings);
        Assert.Null(summary.Summary);
    }
}
