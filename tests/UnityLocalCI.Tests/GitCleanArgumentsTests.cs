using UnityLocalCI.Core.Git;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// Teste de regressao das exclusoes do 'git clean'.
///
/// Se alguem remover uma exclusao, toda build vira build limpa: o cache do Unity
/// e apagado e o tempo sai de ~15 para ~45 minutos. Este teste existe para que
/// isso falhe aqui, e nao em producao tres semanas depois.
/// </summary>
public class GitCleanArgumentsTests
{
    [Theory]
    [InlineData("Library/")]
    [InlineData("Temp/")]
    [InlineData("obj/")]
    [InlineData("Logs/")]
    [InlineData("UserSettings/")]
    public void Clean_preserva_o_cache_do_unity(string preserved)
    {
        var args = GitCleanArguments.Build();

        var index = args.ToList().IndexOf(preserved);
        Assert.True(index > 0, $"'{preserved}' precisa estar nas exclusoes do git clean.");
        Assert.Equal("-e", args[index - 1]);
    }

    [Fact]
    public void Clean_usa_xdf_e_exclui_exatamente_os_cinco_caminhos()
    {
        var args = GitCleanArguments.Build();

        Assert.Equal("clean", args[0]);
        Assert.Equal("-xdf", args[1]);

        var excluded = args.Where((_, i) => i > 0 && args[i - 1] == "-e").ToArray();
        Assert.Equal(
            new[] { "Library/", "Temp/", "obj/", "Logs/", "UserSettings/" },
            excluded);
    }

    [Fact]
    public void Clean_nunca_roda_sem_exclusoes()
    {
        var args = GitCleanArguments.Build();
        Assert.Contains("-e", args);
        Assert.NotEmpty(GitCleanArguments.PreservedPaths);
    }
}
