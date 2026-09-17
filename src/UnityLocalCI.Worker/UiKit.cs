using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace UnityLocalCI.App;

/// <summary>
/// Os controles desenhados a mao.
///
/// O WinForms entrega botoes e paineis com a cara do Windows XP tardio. Como o
/// objetivo aqui e uma janela atual — superficies arredondadas, hierarquia por
/// tom, um destaque so — cada um destes assume o proprio desenho. Todos usam
/// <see cref="TextRenderer"/> e nao Graphics.DrawString: o texto sai com o
/// mesmo peso do resto do sistema, em vez de esmaecido.
/// </summary>
internal static class UiKit
{
    public const TextFormatFlags LeftMiddle =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
        TextFormatFlags.EndEllipsis;

    public const TextFormatFlags Centered =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
        TextFormatFlags.EndEllipsis;

    public static void Text(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags)
        => TextRenderer.DrawText(g, text, font, bounds, color, flags);
}

// ---------------------------------------------------------------------- cartao

/// <summary>
/// Superficie arredondada que agrupa conteudo. E o unico recurso de
/// profundidade da janela: um degrau de tom e um contorno discreto, sem sombra
/// falsa.
/// </summary>
internal class Card : Panel, IPaintsItself
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        // Os filhos leem Parent.BackColor para saber sobre o que estao: precisa
        // ser a cor pintada por dentro, nao a de fora dos cantos.
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Padding = new Padding(1);
    }

    /// <summary>Cor de fora dos cantos arredondados — o que aparece atras do cartao.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Outer { get; init; } = Theme.Canvas;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Radius { get; init; } = 10f;

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var fundo = new SolidBrush(Outer))
            e.Graphics.FillRectangle(fundo, ClientRectangle);

        var bounds = ClientRectangle;
        bounds.Width -= 1;
        bounds.Height -= 1;

        Theme.PaintCard(e.Graphics, bounds, BackColor, Radius);
    }
}

// ---------------------------------------------------------------------- botao

internal enum ButtonKind
{
    /// <summary>A acao principal da tela. No maximo uma por tela.</summary>
    Primary,

    Default,

    /// <summary>Sem preenchimento: acoes secundarias que nao devem competir.</summary>
    Ghost,
}

/// <summary>
/// Botao arredondado com estados de hover e pressionado.
///
/// Herda de Button para nao quebrar nada: Text, Click e Enabled continuam
/// valendo, so a pintura muda.
/// </summary>
internal sealed class PillButton : Button, IPaintsItself
{
    private bool _hover;
    private bool _pressed;

    public PillButton(string text, ButtonKind kind = ButtonKind.Default)
    {
        Kind = kind;
        Text = text;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Ui;
        Height = 32;
        Margin = new Padding(0, 0, 8, 0);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    public ButtonKind Kind { get; }

    /// <summary>Cor atras do botao, para os cantos arredondados.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Backdrop { get; init; }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var fundo = new SolidBrush(Backdrop ?? Parent?.BackColor ?? Theme.Canvas))
            g.FillRectangle(fundo, ClientRectangle);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var (preenchimento, texto, contorno) = Colors();

        if (preenchimento.A > 0)
            Theme.FillRounded(g, bounds, preenchimento, 7f);

        if (contorno.A > 0)
            Theme.DrawRounded(g, bounds, contorno, 7f);

        UiKit.Text(g, Text, Font, ClientRectangle, texto, UiKit.Centered);

        if (Focused && Enabled)
            Theme.DrawRounded(g, Rectangle.Inflate(bounds, -2, -2), Theme.Blend(preenchimento, Theme.Accent, 0.5), 5f);
    }

    private (Color Fill, Color Text, Color Border) Colors()
    {
        if (!Enabled)
            return Kind == ButtonKind.Ghost
                ? (Color.Transparent, Theme.TextFaint, Color.Transparent)
                : (Theme.Blend(Theme.Canvas, Theme.Surface, 0.6), Theme.TextFaint, Theme.BorderSoft);

        return Kind switch
        {
            ButtonKind.Primary => (
                _pressed ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent,
                Color.FromArgb(0x1A, 0x14, 0x11),
                Color.Transparent),

            ButtonKind.Ghost => (
                _pressed ? Theme.SurfaceHigh : _hover ? Theme.Blend(Theme.Canvas, Theme.Text, 0.07) : Color.Transparent,
                _hover ? Theme.Text : Theme.TextMuted,
                Color.Transparent),

            _ => (
                _pressed ? Theme.Blend(Theme.SurfaceHigh, Theme.Text, 0.12)
                         : _hover ? Theme.Blend(Theme.SurfaceHigh, Theme.Text, 0.07)
                         : Theme.SurfaceHigh,
                Theme.Text,
                Theme.Border),
        };
    }
}

// ----------------------------------------------------------------- lista

/// <summary>
/// Lista com a selecao do tema em vez da azul do Windows.
///
/// O ListBox pinta a linha selecionada com a cor de destaque do sistema, que
/// aqui aparecia como uma barra azul viva no meio de uma tela quente e escura.
/// </summary>
internal sealed class DarkListBox : ListBox, IPaintsItself
{
    public DarkListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        ItemHeight = 26;
        IntegralHeight = false;

        Theme.UseDarkScrollbars(this);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;

        var g = e.Graphics;
        var selecionado = (e.State & DrawItemState.Selected) != 0;

        using (var fundo = new SolidBrush(Theme.Surface))
            g.FillRectangle(fundo, e.Bounds);

        if (selecionado)
            Theme.FillRounded(g, Rectangle.Inflate(e.Bounds, -2, -1), Theme.AccentSoft, 6f);

        UiKit.Text(g, Items[e.Index]?.ToString() ?? "", selecionado ? Theme.UiBold : Theme.Ui,
            new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height),
            selecionado ? Theme.Text : Theme.TextMuted, UiKit.LeftMiddle);
    }
}

// ------------------------------------------------------------------ navegacao

internal enum NavGlyph { Projects, Builds, Log, Tutorial, Settings }

/// <summary>
/// Coluna de navegacao a esquerda, no lugar das abas de cima.
///
/// Abas empilhadas horizontalmente ficam apertadas e nao tem lugar para
/// identidade nenhuma; a coluna tem espaco para o nome do programa, para os
/// icones e para crescer sem reflow.
/// </summary>
internal sealed class NavRail : Panel, IPaintsItself
{
    private readonly List<NavItem> _items = [];
    private int _selected = -1;

    public NavRail()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        BackColor = Theme.Rail;
        Width = 208;
        Padding = new Padding(10, 68, 10, 10);
    }

    public event Action<int>? SelectionChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string HeaderTitle { get; init; } = "UnityLocalCI";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string HeaderSubtitle { get; init; } = "CI local";

    /// <summary>Marca do programa desenhada no topo — a mesma da bandeja.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image? HeaderMark { get; init; }

    public void AddItem(string text, NavGlyph glyph)
    {
        var item = new NavItem(text, glyph) { Dock = DockStyle.Top };
        var index = _items.Count;
        item.Click += (_, _) => SelectedIndex = index;

        _items.Add(item);

        // Dock=Top empilha na ordem inversa da insercao; inserir no inicio da
        // colecao devolve a ordem de leitura.
        Controls.Add(item);
        Controls.SetChildIndex(item, 0);

        if (index == 0) SelectedIndex = 0;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _items.Count || value == _selected) return;

            _selected = value;
            for (var i = 0; i < _items.Count; i++) _items[i].Selected = i == value;

            SelectionChanged?.Invoke(value);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var fundo = new SolidBrush(Theme.Rail))
            g.FillRectangle(fundo, ClientRectangle);

        // Filete separando a coluna do conteudo: sem ele os dois tons proximos
        // viram uma mancha so.
        using (var borda = new Pen(Theme.BorderSoft))
            g.DrawLine(borda, Width - 1, 0, Width - 1, Height);

        var textoX = 16;

        if (HeaderMark is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(HeaderMark, new Rectangle(14, 18, 24, 24));
            textoX = 46;
        }

        // Altura folgada de proposito: com o retangulo justo, o Windows corta a
        // perna do 'y' de Unity.
        UiKit.Text(g, HeaderTitle, Theme.UiBold, new Rectangle(textoX, 14, Width - textoX - 10, 18),
            Theme.Text, UiKit.LeftMiddle);

        UiKit.Text(g, HeaderSubtitle, Theme.UiSmall, new Rectangle(textoX, 32, Width - textoX - 10, 16),
            Theme.TextFaint, UiKit.LeftMiddle);
    }
}

internal sealed class NavItem : Control, IPaintsItself
{
    private readonly NavGlyph _glyph;
    private bool _hover;
    private bool _selected;

    public NavItem(string text, NavGlyph glyph)
    {
        _glyph = glyph;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Text = text;
        Height = 38;
        Cursor = Cursors.Hand;
        Font = Theme.Ui;
        BackColor = Theme.Rail;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var fundo = new SolidBrush(Theme.Rail))
            g.FillRectangle(fundo, ClientRectangle);

        var bounds = new Rectangle(0, 2, Width, Height - 4);

        if (_selected) Theme.FillRounded(g, bounds, Theme.AccentSoft, 8f);
        else if (_hover) Theme.FillRounded(g, bounds, Theme.Blend(Theme.Rail, Theme.Text, 0.06), 8f);

        var cor = _selected ? Theme.Accent : _hover ? Theme.Text : Theme.TextMuted;

        DrawGlyph(g, new Rectangle(12, bounds.Y + (bounds.Height - 16) / 2, 16, 16), cor);

        UiKit.Text(g, Text, _selected ? Theme.UiBold : Theme.Ui,
            new Rectangle(38, bounds.Y, Width - 46, bounds.Height),
            _selected ? Theme.Text : cor, UiKit.LeftMiddle);
    }

    /// <summary>
    /// Os icones sao desenhados e nao vem de uma fonte de simbolos: fonte de
    /// icone que nao existe na maquina vira quadradinho, e a lista de simbolos
    /// muda entre versoes do Windows.
    /// </summary>
    private void DrawGlyph(Graphics g, Rectangle r, Color color)
    {
        var modo = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var caneta = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var pincel = new SolidBrush(color);

        switch (_glyph)
        {
            // Quatro blocos: a visao geral dos projetos.
            case NavGlyph.Projects:
                var lado = (r.Width - 3) / 2f;
                foreach (var (dx, dy) in new[] { (0f, 0f), (lado + 3, 0f), (0f, lado + 3), (lado + 3, lado + 3) })
                    using (var p = Theme.RoundedPath(new RectangleF(r.X + dx, r.Y + dy, lado, lado), 1.5f))
                        g.DrawPath(caneta, p);
                break;

            // Camadas empilhadas: o historico de builds.
            case NavGlyph.Builds:
                using (var topo = Theme.RoundedPath(new RectangleF(r.X, r.Y + 1, r.Width, r.Height / 2.6f), 2f))
                    g.DrawPath(caneta, topo);
                g.DrawLine(caneta, r.X + 2, r.Y + r.Height * 0.62f, r.Right - 2, r.Y + r.Height * 0.62f);
                g.DrawLine(caneta, r.X + 2, r.Y + r.Height * 0.88f, r.Right - 2, r.Y + r.Height * 0.88f);
                break;

            // Terminal: o log do servico.
            case NavGlyph.Log:
                using (var caixa = Theme.RoundedPath(new RectangleF(r.X, r.Y + 1, r.Width, r.Height - 2), 2.5f))
                    g.DrawPath(caneta, caixa);
                g.DrawLines(caneta,
                [
                    new PointF(r.X + 4, r.Y + 5.5f),
                    new PointF(r.X + 6.5f, r.Y + 8),
                    new PointF(r.X + 4, r.Y + 10.5f),
                ]);
                g.DrawLine(caneta, r.X + 8.5f, r.Y + 10.5f, r.Right - 3.5f, r.Y + 10.5f);
                break;

            // Livro aberto: o tutorial.
            case NavGlyph.Tutorial:
                var meio = r.X + r.Width / 2f;
                g.DrawLines(caneta,
                [
                    new PointF(r.X + 1, r.Y + 3),
                    new PointF(meio - 0.5f, r.Y + 4.5f),
                    new PointF(meio - 0.5f, r.Bottom - 2),
                    new PointF(r.X + 1, r.Bottom - 3.5f),
                ]);
                g.DrawLines(caneta,
                [
                    new PointF(r.Right - 1, r.Y + 3),
                    new PointF(meio + 0.5f, r.Y + 4.5f),
                    new PointF(meio + 0.5f, r.Bottom - 2),
                    new PointF(r.Right - 1, r.Bottom - 3.5f),
                ]);
                g.DrawLine(caneta, r.X + 1, r.Y + 3, r.X + 1, r.Bottom - 3.5f);
                g.DrawLine(caneta, r.Right - 1, r.Y + 3, r.Right - 1, r.Bottom - 3.5f);
                break;

            // Dois cursores: a configuracao.
            default:
                g.DrawLine(caneta, r.X + 1, r.Y + 5, r.Right - 1, r.Y + 5);
                g.DrawLine(caneta, r.X + 1, r.Y + 11, r.Right - 1, r.Y + 11);
                g.FillEllipse(pincel, r.X + 3.5f, r.Y + 2.5f, 5f, 5f);
                g.FillEllipse(pincel, r.Right - 8.5f, r.Y + 8.5f, 5f, 5f);
                break;
        }

        g.SmoothingMode = modo;
    }
}

// -------------------------------------------------------------- cabecalho

/// <summary>
/// Titulo e subtitulo da pagina, com as acoes dela a direita.
///
/// As acoes ficam aqui, e nao numa barra propria embaixo: no cabecalho elas
/// pertencem visivelmente a pagina aberta, e sobra altura para o conteudo.
/// </summary>
internal sealed class PageHeader : Panel, IPaintsItself
{
    private readonly string _title;
    private readonly string _subtitle;

    public PageHeader(string title, string subtitle)
    {
        _title = title;
        _subtitle = subtitle;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Dock = DockStyle.Top;
        Height = 62;
        BackColor = Theme.Canvas;

        Actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 15, 0, 0),
            BackColor = Theme.Canvas,
            WrapContents = false,
        };

        Controls.Add(Actions);
    }

    /// <summary>Preenchida da direita para a esquerda: o primeiro adicionado fica mais a direita.</summary>
    public FlowLayoutPanel Actions { get; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var fundo = new SolidBrush(Theme.Canvas))
            g.FillRectangle(fundo, ClientRectangle);

        var largura = Width - Actions.Width - 16;

        UiKit.Text(g, _title, Theme.Title, new Rectangle(0, 10, largura, 24), Theme.Text, UiKit.LeftMiddle);
        UiKit.Text(g, _subtitle, Theme.UiSmall, new Rectangle(0, 34, largura, 16), Theme.TextMuted, UiKit.LeftMiddle);
    }
}

// ------------------------------------------------------------- barra de estado

/// <summary>
/// Rodape: um ponto colorido com o estado do servico, o texto ao lado e os
/// numeros da fila a direita. O ponto existe para o estado ser lido de relance,
/// sem precisar ler a frase.
/// </summary>
internal sealed class StatusBar : Control, IPaintsItself
{
    private string _text = "";
    private string _right = "";
    private Color _dot = Theme.TextFaint;

    public StatusBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Dock = DockStyle.Bottom;
        Height = 34;
        BackColor = Theme.Canvas;
        Font = Theme.Ui;
    }

    public void Set(string text, Color dot, string right = "")
    {
        _text = text;
        _dot = dot;
        _right = right;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var fundo = new SolidBrush(Theme.Canvas))
            g.FillRectangle(fundo, ClientRectangle);

        using (var borda = new Pen(Theme.BorderSoft))
            g.DrawLine(borda, 0, 0, Width, 0);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var ponto = new SolidBrush(_dot))
            g.FillEllipse(ponto, 2, Height / 2f - 3.5f, 7, 7);
        g.SmoothingMode = SmoothingMode.Default;

        var direita = TextRenderer.MeasureText(_right, Theme.UiSmall).Width;

        UiKit.Text(g, _text, Theme.Ui, new Rectangle(16, 0, Width - direita - 24, Height),
            Theme.TextMuted, UiKit.LeftMiddle);

        if (_right.Length > 0)
            UiKit.Text(g, _right, Theme.UiSmall, new Rectangle(Width - direita - 2, 0, direita, Height),
                Theme.TextFaint, UiKit.LeftMiddle);
    }
}
