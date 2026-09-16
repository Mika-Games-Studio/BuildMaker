namespace UnityLocalCI.App;

/// <summary>
/// TabControl que pinta a si mesmo.
///
/// Desenhar so os itens por OwnerDrawFixed nao basta: a faixa atras das abas
/// continua sendo pintada pelo tema do sistema, e sobra uma barra clara a
/// direita da ultima aba. Assumindo o desenho inteiro com UserPaint, nao sobra
/// nada do tema claro.
/// </summary>
internal sealed class DarkTabControl : TabControl
{
    public DarkTabControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);

        DrawMode = TabDrawMode.OwnerDrawFixed;
        Padding = new Point(16, 5);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;

        using (var background = new SolidBrush(Theme.Background))
            graphics.FillRectangle(background, ClientRectangle);

        // Linha separando a faixa das abas do conteudo, para a aba ativa nao
        // parecer flutuando sobre a grade.
        var stripBottom = DisplayRectangle.Top - 2;
        using (var border = new Pen(Theme.Border))
            graphics.DrawLine(border, 0, stripBottom, Width, stripBottom);

        for (var index = 0; index < TabCount; index++)
            DrawTab(graphics, index);
    }

    private void DrawTab(Graphics graphics, int index)
    {
        var bounds = GetTabRect(index);
        var selected = SelectedIndex == index;

        using var background = new SolidBrush(selected ? Theme.Surface : Theme.Background);
        using var foreground = new SolidBrush(selected ? Theme.Text : Theme.TextMuted);

        graphics.FillRectangle(background, bounds);

        if (selected)
        {
            // Filete no topo em vez de moldura inteira: marca a aba ativa sem
            // brigar com a borda da grade logo abaixo.
            using var accent = new SolidBrush(Theme.Selection);
            graphics.FillRectangle(accent, bounds.X, bounds.Y, bounds.Width, 2);
        }

        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        graphics.DrawString(TabPages[index].Text, Font, foreground, bounds, format);
    }
}
