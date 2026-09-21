using System.Drawing.Drawing2D;

namespace UnityLocalCI.App;

/// <summary>
/// Quem esta conectado, com foto e @, do jeito que o GitHub mostra.
///
/// E confirmacao visual: "conectado" sozinho nao diz se o token e o do time ou
/// o de uma conta pessoal esquecida nesta maquina — a foto e o @ dizem na
/// hora. Sem conexao, fica a silhueta, que ja e a resposta.
/// </summary>
internal sealed class AccountCard : Control, IPaintsItself
{
    private const int Foto = 52;
    private const int Margem = 14;

    private Image? _retrato;
    private string _titulo = "Nenhuma conta conectada";
    private string? _detalhe;
    private string? _rodape;
    private Color _corDoTitulo = Theme.TextMuted;

    public AccountCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Height = 84;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
    }

    /// <summary>
    /// Troca o que a tela mostra. O retrato anterior e descartado aqui: cada
    /// conexao traz um bitmap novo, e guardar todos vazaria memoria numa janela
    /// que fica aberta o dia inteiro.
    /// </summary>
    public void Mostrar(string titulo, string? detalhe, string? rodape, Image? retrato, bool conectado)
    {
        if (!ReferenceEquals(_retrato, retrato))
        {
            _retrato?.Dispose();
            _retrato = retrato;
        }

        _titulo = titulo;
        _detalhe = detalhe;
        _rodape = rodape;
        _corDoTitulo = conectado ? Theme.Text : Theme.TextMuted;

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var fundo = new SolidBrush(Parent?.BackColor ?? Theme.Surface))
            g.FillRectangle(fundo, ClientRectangle);

        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRounded(g, area, Theme.SurfaceHigh, 10f);
        Theme.DrawRounded(g, area, Theme.BorderSoft, 10f);

        var circulo = new Rectangle(Margem, (Height - Foto) / 2, Foto, Foto);
        DesenharRetrato(g, circulo);

        var textoX = circulo.Right + 14;
        var largura = Width - textoX - Margem;
        if (largura < 40) return;

        var alturaDoTitulo = TextRenderer.MeasureText("Ay", Theme.UiBold).Height;
        var alturaDaLinha = TextRenderer.MeasureText("Ay", Theme.UiSmall).Height;

        var total = alturaDoTitulo
                    + (_detalhe is null ? 0 : alturaDaLinha + 2)
                    + (_rodape is null ? 0 : alturaDaLinha + 2);

        var y = (Height - total) / 2;

        TextRenderer.DrawText(g, _titulo, Theme.UiBold, new Rectangle(textoX, y, largura, alturaDoTitulo),
            _corDoTitulo, TextFormatFlags.EndEllipsis);
        y += alturaDoTitulo + 2;

        if (_detalhe is not null)
        {
            TextRenderer.DrawText(g, _detalhe, Theme.UiSmall, new Rectangle(textoX, y, largura, alturaDaLinha),
                Theme.TextMuted, TextFormatFlags.EndEllipsis);
            y += alturaDaLinha + 2;
        }

        if (_rodape is not null)
            TextRenderer.DrawText(g, _rodape, Theme.UiSmall, new Rectangle(textoX, y, largura, alturaDaLinha),
                Theme.TextFaint, TextFormatFlags.EndEllipsis);
    }

    private void DesenharRetrato(Graphics g, Rectangle circulo)
    {
        if (_retrato is not null)
        {
            using var recorte = new GraphicsPath();
            recorte.AddEllipse(circulo);

            var anterior = g.Clip;
            g.SetClip(recorte);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_retrato, circulo);
            g.Clip = anterior;

            using var borda = new Pen(Theme.Border);
            g.DrawEllipse(borda, circulo);
            return;
        }

        // Silhueta: cabeca e ombros, no mesmo cinza do texto apagado.
        using (var fundo = new SolidBrush(Theme.Blend(Theme.SurfaceHigh, Theme.Text, 0.08)))
            g.FillEllipse(fundo, circulo);

        var cabeca = new Rectangle(
            circulo.X + circulo.Width * 33 / 100,
            circulo.Y + circulo.Height * 22 / 100,
            circulo.Width * 34 / 100,
            circulo.Height * 34 / 100);

        var ombros = new Rectangle(
            circulo.X + circulo.Width * 18 / 100,
            circulo.Y + circulo.Height * 62 / 100,
            circulo.Width * 64 / 100,
            circulo.Height * 50 / 100);

        using var tinta = new SolidBrush(Theme.TextFaint);
        g.FillEllipse(tinta, cabeca);

        using var recorteDoCirculo = new GraphicsPath();
        recorteDoCirculo.AddEllipse(circulo);

        var guardado = g.Clip;
        g.SetClip(recorteDoCirculo);
        g.FillEllipse(tinta, ombros);
        g.Clip = guardado;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _retrato?.Dispose();
        base.Dispose(disposing);
    }
}
