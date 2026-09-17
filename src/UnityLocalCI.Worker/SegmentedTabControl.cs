namespace UnityLocalCI.App;

/// <summary>
/// TabControl pintado como um seletor segmentado — a faixa arredondada com o
/// segmento ativo em destaque, como nos aplicativos atuais.
///
/// Desenhar so os itens por OwnerDrawFixed nao basta: a faixa atras das abas
/// continua sendo pintada pelo tema do sistema, e sobra uma barra clara a
/// direita da ultima aba. Assumindo o desenho inteiro com UserPaint, nao sobra
/// nada do tema do Windows.
///
/// Fica na aba de configuracao. A navegacao principal e a coluna da esquerda:
/// aba dentro de aba confunde, entao aqui as duas linguagens sao diferentes de
/// proposito.
/// </summary>
internal sealed class SegmentedTabControl : TabControl, IPaintsItself
{
    private const int TrackPadding = 3;

    private int _hovered = -1;

    public SegmentedTabControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);

        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(120, 28);
        Padding = new Point(0, 0);
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var sob = -1;
        for (var i = 0; i < TabCount; i++)
            if (GetTabRect(i).Contains(e.Location)) { sob = i; break; }

        if (sob != _hovered) { _hovered = sob; Invalidate(); }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hovered != -1) { _hovered = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var fundo = new SolidBrush(Theme.Canvas))
            g.FillRectangle(fundo, ClientRectangle);

        if (TabCount == 0) return;

        // A faixa envolve todas as abas de uma vez: e ela que da a leitura de
        // "um controle", em vez de botoes soltos.
        var primeira = GetTabRect(0);
        var ultima = GetTabRect(TabCount - 1);
        var faixa = Rectangle.FromLTRB(
            primeira.Left - TrackPadding, primeira.Top - TrackPadding,
            ultima.Right + TrackPadding, ultima.Bottom + TrackPadding);

        Theme.FillRounded(g, faixa, Theme.Blend(Theme.Canvas, Theme.Surface, 0.9), 9f);
        Theme.DrawRounded(g, faixa, Theme.BorderSoft, 9f);

        for (var index = 0; index < TabCount; index++)
            DrawTab(g, index);
    }

    private void DrawTab(Graphics graphics, int index)
    {
        var bounds = GetTabRect(index);
        var selected = SelectedIndex == index;

        if (selected)
        {
            Theme.FillRounded(graphics, Rectangle.Inflate(bounds, -1, -1), Theme.SurfaceHigh, 7f);
            Theme.DrawRounded(graphics, Rectangle.Inflate(bounds, -1, -1), Theme.Border, 7f);
        }
        else if (_hovered == index)
        {
            Theme.FillRounded(graphics, Rectangle.Inflate(bounds, -1, -1),
                Theme.Blend(Theme.Canvas, Theme.Text, 0.05), 7f);
        }

        var cor = selected ? Theme.Text : _hovered == index ? Theme.Text : Theme.TextMuted;

        UiKit.Text(graphics, TabPages[index].Text, selected ? Theme.UiBold : Theme.Ui, bounds, cor, UiKit.Centered);
    }
}
