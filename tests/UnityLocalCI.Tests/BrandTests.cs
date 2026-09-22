using System.Reflection;
using UnityLocalCI.App;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// A identidade visual, na parte que uma captura de tela nao pega.
///
/// O documento de marca poe uma regra dura: nenhuma cor fora da escala. Uma
/// regra assim so sobrevive se algo a verificar — em WinForms cada controle
/// escolhe a propria cor, e basta um <c>Color.FromArgb</c> apressado num canto
/// da tela para a paleta virar sugestao.
/// </summary>
public class BrandTests
{
    /// <summary>
    /// A escala inteira do documento: verde 100 a 900, os neutros do mesmo
    /// matiz, e os quatro estados de build. Nada mais.
    /// </summary>
    private static readonly HashSet<int> Escala =
    [
        // verde 100..900
        0xEDF6DF, 0xC9E59E, 0xA1D651, 0x89CE22, 0x6FAB16, 0x5B8D11, 0x4A730D, 0x324E09, 0x1D2D06,

        // o verde 500 diluido no fundo, que o documento lista como token proprio
        0x1D280B,

        // neutros
        0xF7FAF1, 0xE9EFDD, 0xA9B594, 0x7C8A68, 0x4F5F38, 0x2E3A1A, 0x1B2410, 0x12160A, 0x0A0D05,
        0x000000, 0x060802,

        // estados de build
        0x3FB58E, 0xE2593C, 0xE0A21C, 0x62A8C4,

        // A area de log, que o documento de telas especifica a parte: um fundo
        // proprio meio degrau acima do cartao, e as versoes claras de erro e
        // aviso para a mensagem — a cor cheia fica no rotulo, que e curto.
        0x171F0B, 0xF3A08D, 0xEDC66E,
    ];

    private static IEnumerable<(string Nome, Color Cor)> CoresDoTema()
        => typeof(Theme)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(campo => campo.FieldType == typeof(Color))
            .Select(campo => (campo.Name, (Color)campo.GetValue(null)!));

    [Fact]
    public void Nenhuma_cor_do_tema_esta_fora_da_escala_da_marca()
    {
        var fora = CoresDoTema()
            .Where(c => !Escala.Contains(c.Cor.ToArgb() & 0xFFFFFF))
            .Select(c => $"{c.Nome} = #{c.Cor.R:X2}{c.Cor.G:X2}{c.Cor.B:X2}")
            .ToList();

        Assert.Empty(fora);
    }

    /// <summary>
    /// O verde da marca e claro: texto branco em cima dele da 2,7:1, que nao se
    /// le. O que vai por cima e o preto.
    /// </summary>
    [Fact]
    public void Texto_sobre_o_verde_passa_em_contraste()
        => Assert.True(Contraste(Theme.Canvas, Theme.Accent) >= 4.5,
            $"contraste de {Contraste(Theme.Canvas, Theme.Accent):0.0}:1");

    /// <summary>
    /// O caminho contrario: quando o verde e a tinta sobre o fundo preto, o 500
    /// fica no limite e quem entra e o 400.
    /// </summary>
    [Fact]
    public void Verde_como_tinta_e_mais_claro_que_o_verde_de_preenchimento()
    {
        Assert.True(Contraste(Theme.AccentHover, Theme.Canvas) > Contraste(Theme.Accent, Theme.Canvas));
        Assert.True(Contraste(Theme.AccentHover, Theme.Canvas) >= 4.5);
    }

    /// <summary>
    /// Sucesso nao pode ser o verde da marca: se fossem o mesmo, "terminou bem"
    /// e "este e o botao principal" falariam com a mesma voz.
    /// </summary>
    [Fact]
    public void Sucesso_nao_e_o_verde_da_marca()
        => Assert.NotEqual(Theme.Accent.ToArgb(), Theme.Success.ToArgb());

    private static double Contraste(Color a, Color b)
    {
        var (claro, escuro) = Luminancia(a) > Luminancia(b)
            ? (Luminancia(a), Luminancia(b))
            : (Luminancia(b), Luminancia(a));

        return (claro + 0.05) / (escuro + 0.05);
    }

    private static double Luminancia(Color cor)
        => 0.2126 * Canal(cor.R) + 0.7152 * Canal(cor.G) + 0.0722 * Canal(cor.B);

    private static double Canal(int valor)
    {
        var v = valor / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    // ------------------------------------------------------------------ marca

    /// <summary>
    /// O cubo e desenhado dentro do quadro que lhe foi dado, e nao um pixel
    /// alem: o icone o coloca sobre um azulejo de canto arredondado, e o que
    /// escapasse do quadro seria cortado pelo canto.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_marca_cabe_no_quadro(bool compacta)
    {
        using var bitmap = BrandMark.Desenhar(64, Color.Lime, Color.Green, Color.Black, compacta);

        Assert.Equal(64, bitmap.Width);
        Assert.Equal(64, bitmap.Height);

        // Os quatro cantos ficam de fora do cubo isometrico, em qualquer das
        // duas versoes: se algum deles tiver tinta, o desenho vazou.
        foreach (var (x, y) in new[] { (0, 0), (63, 0), (0, 63), (63, 63) })
            Assert.Equal(0, bitmap.GetPixel(x, y).A);
    }

    /// <summary>
    /// O vao nao e desenho a mais: e a ausencia de um bloco. O canto de cima a
    /// direita do cubo tem de estar vazio — e por ali que se ve que falta uma
    /// peca.
    /// </summary>
    [Fact]
    public void O_encaixe_da_versao_compacta_esta_vazio()
    {
        using var bitmap = BrandMark.Desenhar(200, Color.Lime, Color.Green, Color.Black, compacta: true);

        // Dentro do vao, no quadro de 100: (75, 28). O centro exato do losango
        // cai na aresta do bloco vizinho, e a aresta e pintada.
        Assert.Equal(0, bitmap.GetPixel(150, 56).A);

        // E o bloco de cima a esquerda, que existe, esta pintado.
        Assert.NotEqual(0, bitmap.GetPixel(62, 56).A);
    }

    /// <summary>
    /// So a versao completa traz a peca solta. Abaixo de 32 pixels ela vira um
    /// ponto perdido, e por isso a compacta nao a desenha.
    /// </summary>
    [Fact]
    public void So_a_marca_completa_tem_a_peca_solta()
    {
        using var completa = BrandMark.Desenhar(200, Color.Lime, Color.Green, Color.Black, compacta: false);
        using var compacta = BrandMark.Desenhar(200, Color.Lime, Color.Green, Color.Black, compacta: true);

        // Face de cima da peca solta, no quadro de 100: (77, 20).
        Assert.NotEqual(0, completa.GetPixel(154, 40).A);
        Assert.Equal(0, compacta.GetPixel(154, 40).A);
    }
}
