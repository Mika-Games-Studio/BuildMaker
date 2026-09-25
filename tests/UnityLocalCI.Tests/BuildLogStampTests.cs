using UnityLocalCI.Core.Pipeline;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O carimbo de hora do log de build.
///
/// A janela separa a coluna de hora contando caracteres, entao o que importa
/// aqui e o que acontece quando a linha nao tem carimbo: log de uma versao
/// anterior, ou uma linha do Unity que por acaso comece com numeros. Cortar
/// oito caracteres a esmo comeria o inicio da mensagem, e a mensagem de erro e
/// justamente o que se esta procurando.
/// </summary>
public class BuildLogStampTests
{
    [Fact]
    public void Linha_carimbada_se_divide_em_hora_e_mensagem()
    {
        var linha = BuildLogStamp.Prefix(new DateTimeOffset(2026, 9, 22, 14, 6, 31, TimeSpan.Zero).ToLocalTime()) +
                    "Compilando scripts";

        var (hora, resto) = BuildLogStamp.Split(linha);

        Assert.Equal(8, hora.Length);
        Assert.Equal("Compilando scripts", resto);
    }

    [Fact]
    public void Linha_sem_carimbo_vem_inteira()
    {
        var (hora, resto) = BuildLogStamp.Split("Assets/Scripts/Spawner.cs(42,17): error CS0246");

        Assert.Equal("", hora);
        Assert.Equal("Assets/Scripts/Spawner.cs(42,17): error CS0246", resto);
    }

    /// <summary>
    /// O Unity escreve linhas que comecam com numero. Nenhuma delas pode ser
    /// confundida com hora e perder os primeiros caracteres.
    /// </summary>
    [Theory]
    [InlineData("12345678  algo")]
    [InlineData("12:34:5x  algo")]
    [InlineData("1234:56  algo")]
    [InlineData("curto")]
    [InlineData("")]
    public void Texto_parecido_com_hora_nao_e_cortado(string linha)
    {
        var (hora, resto) = BuildLogStamp.Split(linha);

        Assert.Equal("", hora);
        Assert.Equal(linha, resto);
    }

    [Fact]
    public void O_carimbo_tem_o_tamanho_que_a_janela_espera()
        => Assert.Equal(BuildLogStamp.Length, BuildLogStamp.Prefix(DateTimeOffset.Now).Length);
}
