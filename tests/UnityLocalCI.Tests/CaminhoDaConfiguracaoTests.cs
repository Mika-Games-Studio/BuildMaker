using UnityLocalCI.Cli;
using Xunit;

namespace UnityLocalCI.Tests;

public class CaminhoDaConfiguracaoTests
{
    [Fact]
    public void O_caminho_informado_ganha_de_tudo()
    {
        var informado = Path.Combine(Path.GetTempPath(), "meu-appsettings.json");

        Assert.Equal(Path.GetFullPath(informado), CaminhoDaConfiguracao.Resolver(informado));
    }

    [Fact]
    public void O_caminho_informado_vira_absoluto()
    {
        // A unidade do systemd roda com WorkingDirectory proprio; um caminho
        // relativo resolvido tarde apontaria para outro lugar.
        var resolvido = CaminhoDaConfiguracao.Resolver("config/appsettings.json");

        Assert.True(Path.IsPathRooted(resolvido));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_caminho_informado_cai_no_padrao(string? informado)
    {
        var resolvido = CaminhoDaConfiguracao.Resolver(informado);

        Assert.EndsWith("appsettings.json", resolvido);
        Assert.True(Path.IsPathRooted(resolvido));
    }

    [Fact]
    public void No_windows_o_padrao_fica_ao_lado_do_executavel()
    {
        // A instalacao do Windows e por usuario e a janela grava ali. So vale
        // afirmar isto quando o teste roda no Windows.
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            CaminhoDaConfiguracao.Resolver(null));
    }

    [Fact]
    public void A_pasta_do_usuario_respeita_o_XDG_CONFIG_HOME()
    {
        var anterior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            var escolhida = Path.Combine(Path.GetTempPath(), "xdg-de-teste");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", escolhida);

            Assert.Equal(
                Path.Combine(escolhida, "BuildMaker"),
                CaminhoDaConfiguracao.PastaDeConfiguracaoDoUsuario());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", anterior);
        }
    }

    [Fact]
    public void Sem_XDG_CONFIG_HOME_a_pasta_do_usuario_cai_em_ponto_config()
    {
        var anterior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);

            var pasta = CaminhoDaConfiguracao.PastaDeConfiguracaoDoUsuario();

            Assert.EndsWith(Path.Combine(".config", "BuildMaker"), pasta);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", anterior);
        }
    }
}
