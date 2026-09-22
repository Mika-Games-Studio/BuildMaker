using System.Drawing.Drawing2D;

namespace UnityLocalCI.App;

/// <summary>
/// A marca do BuildMaker: um cubo isometrico 2x2x2 de blocos iguais com o bloco
/// de cima a direita faltando, e a peca que falta descendo ate o encaixe.
///
/// A geometria e uma lista de quadrilateros num quadro de 100x100, na ordem em
/// que sao pintados: cada um e preenchido e contornado antes do proximo, porque
/// e o contorno — na cor do fundo — que abre a junta entre os blocos. Mudar a
/// ordem desmonta o cubo.
///
/// As mesmas coordenadas estao em tools\gerar-icone.ps1, que gera o .ico. Ele
/// roda antes de o projeto compilar, entao nao tem como reaproveitar esta
/// tabela; quem mexer numa precisa mexer na outra.
/// </summary>
public static class BrandMark
{
    /// <summary>
    /// Um bloco do cubo. <paramref name="Encaixe"/> marca as duas faces que
    /// ficam expostas dentro do vao: elas vao num verde mais fechado, e e isso
    /// que da profundidade ao buraco sem gradiente nenhum.
    /// </summary>
    private readonly record struct Bloco(bool Encaixe, float[] Pontos);

    /// <summary>Junta entre blocos, em fracao da aresta: 8,5% na marca completa.</summary>
    private const float JuntaCompleta = 1.64f;

    /// <summary>Na compacta a junta engorda para 13%, senao ela fecha em 16px.</summary>
    private const float JuntaCompacta = 2.86f;

    /// <summary>O cubo com o encaixe vazio e a peca a caminho dele.</summary>
    private static readonly Bloco[] Completa =
    [
        new(false, [39.62f, 35.98f, 56.36f, 45.65f, 39.62f, 55.32f, 22.87f, 45.65f]),
        new(false, [56.36f, 64.99f, 39.62f, 74.66f, 39.62f, 55.32f, 56.36f, 45.65f]),
        new(false, [22.87f, 64.99f, 39.62f, 74.66f, 39.62f, 55.32f, 22.87f, 45.65f]),
        new(false, [39.62f, 16.64f, 56.36f, 26.31f, 39.62f, 35.98f, 22.87f, 26.31f]),
        new(true, [56.36f, 45.65f, 39.62f, 55.32f, 39.62f, 35.98f, 56.36f, 26.31f]),
        new(false, [22.87f, 45.65f, 39.62f, 55.32f, 39.62f, 35.98f, 22.87f, 26.31f]),
        new(false, [22.87f, 45.65f, 39.62f, 55.32f, 22.87f, 64.99f, 6.12f, 55.32f]),
        new(false, [39.62f, 74.66f, 22.87f, 84.33f, 22.87f, 64.99f, 39.62f, 55.32f]),
        new(false, [6.12f, 74.66f, 22.87f, 84.33f, 22.87f, 64.99f, 6.12f, 55.32f]),
        new(true, [56.36f, 45.65f, 73.11f, 55.32f, 56.36f, 64.99f, 39.62f, 55.32f]),
        new(false, [73.11f, 74.66f, 56.36f, 84.33f, 56.36f, 64.99f, 73.11f, 55.32f]),
        new(false, [39.62f, 74.66f, 56.36f, 84.33f, 56.36f, 64.99f, 39.62f, 55.32f]),
        new(false, [22.87f, 26.31f, 39.62f, 35.98f, 22.87f, 45.65f, 6.12f, 35.98f]),
        new(false, [39.62f, 55.32f, 22.87f, 64.99f, 22.87f, 45.65f, 39.62f, 35.98f]),
        new(false, [6.12f, 55.32f, 22.87f, 64.99f, 22.87f, 45.65f, 6.12f, 35.98f]),
        new(false, [39.62f, 55.32f, 56.36f, 64.99f, 39.62f, 74.66f, 22.87f, 64.99f]),
        new(false, [56.36f, 84.33f, 39.62f, 94f, 39.62f, 74.66f, 56.36f, 64.99f]),
        new(false, [22.87f, 84.33f, 39.62f, 94f, 39.62f, 74.66f, 22.87f, 64.99f]),

        // O bloco da frente volta por cima dos que o cercam: e ele que fecha o
        // canto de baixo do cubo.
        new(false, [39.62f, 35.98f, 56.36f, 45.65f, 39.62f, 55.32f, 22.87f, 45.65f]),
        new(false, [56.36f, 64.99f, 39.62f, 74.66f, 39.62f, 55.32f, 56.36f, 45.65f]),
        new(false, [22.87f, 64.99f, 39.62f, 74.66f, 39.62f, 55.32f, 22.87f, 45.65f]),

        // A peca que falta, do tamanho de um bloco, acima e a direita do vao.
        new(false, [77.13f, 6f, 93.88f, 15.67f, 77.13f, 25.34f, 60.38f, 15.67f]),
        new(false, [93.88f, 35.01f, 77.13f, 44.68f, 77.13f, 25.34f, 93.88f, 15.67f]),
        new(false, [60.38f, 35.01f, 77.13f, 44.68f, 77.13f, 25.34f, 60.38f, 15.67f]),
    ];

    /// <summary>
    /// So o cubo, maior no quadro e sem a peca solta. Abaixo de 32 pixels a
    /// peca vira um ponto perdido e as juntas fecham; aqui ela sai de cena e o
    /// cubo ocupa o espaco que sobrou.
    /// </summary>
    private static readonly Bloco[] Compacta =
    [
        new(false, [50f, 28f, 69.05f, 39f, 50f, 50f, 30.95f, 39f]),
        new(false, [69.05f, 61f, 50f, 72f, 50f, 50f, 69.05f, 39f]),
        new(false, [30.95f, 61f, 50f, 72f, 50f, 50f, 30.95f, 39f]),
        new(false, [50f, 6f, 69.05f, 17f, 50f, 28f, 30.95f, 17f]),
        new(true, [69.05f, 39f, 50f, 50f, 50f, 28f, 69.05f, 17f]),
        new(false, [30.95f, 39f, 50f, 50f, 50f, 28f, 30.95f, 17f]),
        new(false, [30.95f, 39f, 50f, 50f, 30.95f, 61f, 11.9f, 50f]),
        new(false, [50f, 72f, 30.95f, 83f, 30.95f, 61f, 50f, 50f]),
        new(false, [11.9f, 72f, 30.95f, 83f, 30.95f, 61f, 11.9f, 50f]),
        new(true, [69.05f, 39f, 88.1f, 50f, 69.05f, 61f, 50f, 50f]),
        new(false, [88.1f, 72f, 69.05f, 83f, 69.05f, 61f, 88.1f, 50f]),
        new(false, [50f, 72f, 69.05f, 83f, 69.05f, 61f, 50f, 50f]),
        new(false, [30.95f, 17f, 50f, 28f, 30.95f, 39f, 11.9f, 28f]),
        new(false, [50f, 50f, 30.95f, 61f, 30.95f, 39f, 50f, 28f]),
        new(false, [11.9f, 50f, 30.95f, 61f, 30.95f, 39f, 11.9f, 28f]),
        new(false, [50f, 50f, 69.05f, 61f, 50f, 72f, 30.95f, 61f]),
        new(false, [69.05f, 83f, 50f, 94f, 50f, 72f, 69.05f, 61f]),
        new(false, [30.95f, 83f, 50f, 94f, 50f, 72f, 30.95f, 61f]),
        new(false, [50f, 28f, 69.05f, 39f, 50f, 50f, 30.95f, 39f]),
        new(false, [69.05f, 61f, 50f, 72f, 50f, 50f, 69.05f, 39f]),
        new(false, [30.95f, 61f, 50f, 72f, 50f, 50f, 30.95f, 39f]),
    ];

    private static Bitmap? _rail;

    /// <summary>
    /// A marca do topo da coluna de navegacao, com a junta na cor da propria
    /// coluna. Desenhada grande e reduzida na hora de pintar: reduzir um
    /// desenho grande sai limpo, ampliar um pequeno sai serrilhado.
    /// </summary>
    public static Image ParaNavegacao()
        => _rail ??= Desenhar(96, Theme.Accent, Theme.AccentDeep, Theme.Rail, compacta: false);

    /// <summary>
    /// Pinta a marca num quadrado transparente.
    /// </summary>
    /// <param name="corpo">Os blocos.</param>
    /// <param name="encaixe">As duas faces expostas dentro do vao.</param>
    /// <param name="junta">A fresta entre blocos: sempre a cor do que esta atras.</param>
    public static Bitmap Desenhar(int lado, Color corpo, Color encaixe, Color junta, bool compacta)
    {
        var blocos = compacta ? Compacta : Completa;
        var escala = lado / 100f;

        var bitmap = new Bitmap(lado, lado);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var caneta = new Pen(junta, (compacta ? JuntaCompacta : JuntaCompleta) * escala)
        {
            LineJoin = LineJoin.Round,
        };

        using var pincelCorpo = new SolidBrush(corpo);
        using var pincelEncaixe = new SolidBrush(encaixe);

        var pontos = new PointF[4];

        foreach (var bloco in blocos)
        {
            for (var i = 0; i < 4; i++)
                pontos[i] = new PointF(bloco.Pontos[i * 2] * escala, bloco.Pontos[i * 2 + 1] * escala);

            g.FillPolygon(bloco.Encaixe ? pincelEncaixe : pincelCorpo, pontos);
            g.DrawPolygon(caneta, pontos);
        }

        return bitmap;
    }
}
