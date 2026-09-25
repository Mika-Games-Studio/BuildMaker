using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace UnityLocalCI.App;

/// <summary>
/// Marca os controles que pintam a si mesmos. O <see cref="Theme.Apply"/> nao
/// mexe nas cores deles: quem desenha o proprio fundo ja sabe o que quer.
/// </summary>
internal interface IPaintsItself;

/// <summary>
/// Tema da janela.
///
/// As duas cores da marca sao o verde #6FAB16 e o preto. O verde esta em 84
/// graus de matiz, e os neutros sao o mesmo matiz com a saturacao quase toda
/// tirada: fundo, superficie e superficie elevada saem de uma familia so, em
/// vez de um cinza unico. E isso que da profundidade sem sombra, e o que
/// impede a tela de parecer chapada. O destaque e um so, o verde, usado com
/// parcimonia: se tudo destaca, nada destaca.
///
/// O verde e claro demais para carregar texto branco — da 2,7:1, reprovado em
/// qualquer leitura. Por isso o que se escreve em cima do <see cref="Accent"/>
/// e sempre o <see cref="Canvas"/>, que da 7,5:1.
///
/// O contrario tambem vale: o verde 500 sobre o fundo preto fica em 4,4:1, no
/// limite para texto pequeno. Quando o verde e a tinta, e nao o preenchimento,
/// quem entra e o <see cref="AccentHover"/>, o 400, que passa de 8:1.
///
/// O WinForms nao tem tema. Cada controle pinta com as cores do sistema, entao
/// tudo aqui e aplicado controle a controle, e os que o sistema insiste em
/// pintar sozinho (abas, barra de titulo, barras de rolagem) tem tratamento
/// proprio.
/// </summary>
public static class Theme
{
    // --------------------------------------------------------------- paleta

    /// <summary>Fundo da janela, atras de tudo. Preto, e nao um cinza escuro.</summary>
    public static readonly Color Canvas = Rgb(0x000000);

    /// <summary>Coluna de navegacao: um degrau abaixo do conteudo.</summary>
    public static readonly Color Rail = Rgb(0x0A0D05);

    /// <summary>Cartoes, grades, caixas de texto.</summary>
    public static readonly Color Surface = Rgb(0x12160A);

    /// <summary>Cabecalhos de coluna, botoes, estado de hover.</summary>
    public static readonly Color SurfaceHigh = Rgb(0x1B2410);

    public static readonly Color Border = Rgb(0x2E3A1A);
    public static readonly Color BorderSoft = Rgb(0x1B2410);

    /// <summary>
    /// Branco levemente esverdeado, e nao puro: texto branco sobre fundo
    /// colorido vibra na borda, e o olho paga por isso numa tela aberta o dia
    /// inteiro.
    /// </summary>
    public static readonly Color Text = Rgb(0xE9EFDD);

    public static readonly Color TextMuted = Rgb(0xA9B594);
    public static readonly Color TextFaint = Rgb(0x7C8A68);

    /// <summary>
    /// O verde da marca, o 500 da escala. Um unico destaque em toda a
    /// interface, e sempre como preenchimento: botao primario, bolinha de
    /// estado, marca.
    /// </summary>
    public static readonly Color Accent = Rgb(0x6FAB16);

    /// <summary>
    /// O verde 400. E o hover do botao primario e, no resto da janela, o verde
    /// como tinta: icone do item ativo, codigo do device flow, build em
    /// execucao. Sobre o preto o 500 fica em 4,4:1 e este passa de 8:1.
    /// </summary>
    public static readonly Color AccentHover = Rgb(0x89CE22);

    public static readonly Color AccentPressed = Rgb(0x5B8D11);

    /// <summary>Verde diluido no fundo: selecao e item ativo, sem berrar.</summary>
    public static readonly Color AccentSoft = Rgb(0x1D280B);

    /// <summary>
    /// O verde 800, so para as duas faces expostas dentro do encaixe da marca.
    /// E o que da profundidade ao vao sem gradiente nenhum.
    /// </summary>
    public static readonly Color AccentDeep = Rgb(0x324E09);

    /// <summary>
    /// Os estados de build, claros e nao escuros: no fundo escuro o tom fechado
    /// some, e resultado de build e justamente o que precisa saltar aos olhos.
    ///
    /// O verde-agua do sucesso e de proposito diferente do verde da marca. Se
    /// fossem o mesmo, "terminou bem" e "este e o botao principal" falariam com
    /// a mesma voz, e uma tela cheia de linhas verdes nao diria mais nada.
    /// </summary>
    public static readonly Color Success = Rgb(0x3FB58E);

    public static readonly Color Danger = Rgb(0xE2593C);
    public static readonly Color Warning = Rgb(0xE0A21C);
    public static readonly Color Info = Rgb(0x62A8C4);

    // ----------------------------------------------------------------- log

    /// <summary>
    /// O fundo da area de log. Meio degrau acima do cartao: o log e um bloco de
    /// texto denso, e um fundo proprio o separa do resto sem precisar de moldura.
    /// </summary>
    public static readonly Color LogSurface = Rgb(0x171F0B);

    /// <summary>
    /// A mensagem de uma linha de erro. O <see cref="Danger"/> cheio fica no
    /// rotulo, que e curto; uma frase inteira naquele vermelho cansa de ler, e
    /// justamente a frase de erro e a que precisa ser lida ate o fim.
    /// </summary>
    public static readonly Color DangerSoft = Rgb(0xF3A08D);

    /// <summary>O mesmo, para aviso.</summary>
    public static readonly Color WarningSoft = Rgb(0xEDC66E);

    // ------------------------------------------------- compatibilidade de nome

    /// <summary>Nome antigo de <see cref="Canvas"/>.</summary>
    public static readonly Color Background = Canvas;

    /// <summary>Nome antigo de <see cref="SurfaceHigh"/>.</summary>
    public static readonly Color Elevated = SurfaceHigh;

    public static readonly Color Selection = AccentSoft;
    public static readonly Color SelectionText = Text;

    // --------------------------------------------------------------- tipografia

    private static readonly string UiFamily = FirstInstalled("Segoe UI Variable Text", "Segoe UI", "Tahoma");
    private static readonly string MonoFamily = FirstInstalled("Cascadia Mono", "Consolas", "Courier New");

    public static readonly Font Ui = new(UiFamily, 9.25f);
    public static readonly Font UiBold = new(UiFamily, 9.25f, FontStyle.Bold);
    public static readonly Font UiSmall = new(UiFamily, 8.25f);
    public static readonly Font UiSmallBold = new(UiFamily, 8.25f, FontStyle.Bold);
    public static readonly Font Title = new(UiFamily, 14f, FontStyle.Bold);
    public static readonly Font Mono = new(MonoFamily, 9f);

    /// <summary>
    /// O corpo dos botoes: meio ponto acima do texto corrente. Rotulo de botao
    /// e alvo de clique, e alvo de clique se le de relance.
    /// </summary>
    public static readonly Font Button = new(UiFamily, 9.5f);

    public static readonly Font ButtonBold = new(UiFamily, 9.5f, FontStyle.Bold);

    /// <summary>Cabecalho de coluna de grade: pequeno, mas em peso cheio.</summary>
    public static readonly Font ColumnHeader = new(UiFamily, 8.25f, FontStyle.Bold);

    /// <summary>
    /// O rotulo de estado nas grades. Caixa alta em corpo pequeno pede peso, ou
    /// vira um borrao.
    /// </summary>
    public static readonly Font StatusLabel = new(UiFamily, 8.25f, FontStyle.Bold);

    private static string FirstInstalled(params string[] candidates)
    {
        using var installed = new InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates.FirstOrDefault(names.Contains) ?? FontFamily.GenericSansSerif.Name;
    }

    // ------------------------------------------------------------- desenho

    private static Color Rgb(int value)
        => Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);

    /// <summary>Mistura duas cores. <paramref name="amount"/> 0 = a, 1 = b.</summary>
    public static Color Blend(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * amount),
            (int)(a.G + (b.G - a.G) * amount),
            (int)(a.B + (b.B - a.B) * amount));
    }

    /// <summary>Retangulo de cantos arredondados, pronto para preencher ou contornar.</summary>
    public static GraphicsPath RoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();

        // Raio maior que a metade do lado vira uma forma invalida (o arco se
        // dobra sobre si mesmo), entao ele e limitado aqui e nao em cada chamada.
        radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));

        if (radius <= 0.5f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    public static void FillRounded(Graphics graphics, RectangleF bounds, Color color, float radius)
    {
        var modo = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = RoundedPath(bounds, radius);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);

        graphics.SmoothingMode = modo;
    }

    public static void DrawRounded(Graphics graphics, RectangleF bounds, Color color, float radius, float width = 1f)
    {
        var modo = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        // Meio pixel para dentro: sem isto o contorno sai borrado entre dois pixels.
        var ajustado = RectangleF.Inflate(bounds, -width / 2f, -width / 2f);

        using var path = RoundedPath(ajustado, radius);
        using var pen = new Pen(color, width);
        graphics.DrawPath(pen, path);

        graphics.SmoothingMode = modo;
    }

    /// <summary>
    /// Cartao: superficie arredondada com contorno discreto. E o unico recurso
    /// de profundidade usado — o WinForms nao desenha sombra de verdade, e uma
    /// sombra falsa em cima de fundo liso fica pior que nenhuma.
    /// </summary>
    public static void PaintCard(Graphics graphics, Rectangle bounds, Color fill, float radius = 10f)
    {
        FillRounded(graphics, bounds, fill, radius);
        DrawRounded(graphics, bounds, Border, radius);
    }

    // ----------------------------------------------------------- aplicacao

    public static void Apply(Control root)
    {
        ApplyTo(root);

        foreach (Control child in root.Controls)
            Apply(child);
    }

    private static void ApplyTo(Control control)
    {
        // Quem pinta a si mesmo ja escolheu as proprias cores.
        if (control is IPaintsItself) return;

        switch (control)
        {
            case DataGridView grid:
                ApplyGrid(grid);
                return;

            case PropertyGrid propertyGrid:
                ApplyPropertyGrid(propertyGrid);
                return;

            case TabControl tabs:
                ApplyTabs(tabs);
                return;

            case Button button:
                ApplyButton(button);
                return;

            case TextBox textBox:
                textBox.BackColor = Surface;
                textBox.ForeColor = Text;
                textBox.BorderStyle = BorderStyle.None;
                UseDarkScrollbars(textBox);
                return;

            case ListBox list:
                list.BackColor = Surface;
                list.ForeColor = Text;
                list.BorderStyle = BorderStyle.None;
                list.Font = Ui;
                UseDarkScrollbars(list);
                return;

            case SplitContainer split:
                split.BackColor = Canvas;
                split.Panel1.BackColor = Canvas;
                split.Panel2.BackColor = Canvas;
                return;

            // A fonte do rotulo nao e tocada: quem criou um titulo escolheu a
            // fonte de proposito, e sobrescrever aqui achatava o tutorial inteiro
            // num tamanho so. Sem fonte propria, ele herda a do formulario, que
            // ja e a do tema.
            case Label label:
                label.BackColor = BackdropOf(label);
                return;

            default:
                control.BackColor = BackdropOf(control);
                control.ForeColor = Text;
                control.Font = Ui;
                return;
        }
    }

    /// <summary>
    /// Sobre o que este controle esta. Um painel dentro de um cartao precisa da
    /// cor do cartao: pintado com a cor do fundo da janela, ele abre um buraco
    /// escuro no meio da superficie clara.
    /// </summary>
    private static Color BackdropOf(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is Card card) return card.BackColor;
            if (parent is NavRail) return Rail;
        }

        return Canvas;
    }

    private static void ApplyGrid(DataGridView grid)
    {
        // Sem isto o Windows pinta o cabecalho com o tema visual e ignora as
        // cores definidas abaixo.
        grid.EnableHeadersVisualStyles = false;

        grid.BackgroundColor = Surface;
        grid.GridColor = BorderSoft;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ForeColor = Text;
        grid.Font = Ui;
        grid.RowTemplate.Height = 38;
        grid.ColumnHeadersHeight = 34;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = AccentSoft;
        grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.DefaultCellStyle.Padding = new Padding(8, 0, 4, 0);

        grid.ColumnHeadersDefaultCellStyle.BackColor = Surface;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextFaint;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextFaint;
        grid.ColumnHeadersDefaultCellStyle.Font = ColumnHeader;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 4, 0);
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;

        grid.RowHeadersDefaultCellStyle.BackColor = Surface;
        grid.RowHeadersDefaultCellStyle.ForeColor = TextMuted;

        // O filete embaixo do cabecalho e mais forte que o das linhas: ele
        // separa o rotulo do dado, e os das linhas so separam dado de dado.
        // O DataGridView nao tem propriedade para isso, entao ele e pintado.
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex != -1 || e.ColumnIndex < 0) return;

            e.Paint(e.ClipBounds, e.PaintParts);

            using var caneta = new Pen(Border);
            e.Graphics!.DrawLine(caneta,
                e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);

            e.Handled = true;
        };

        UseDarkScrollbars(grid);
    }

    private static void ApplyPropertyGrid(PropertyGrid grid)
    {
        grid.BackColor = Surface;
        grid.ViewBackColor = Surface;
        grid.ViewForeColor = Text;
        grid.ViewBorderColor = BorderSoft;
        grid.LineColor = BorderSoft;
        grid.CategoryForeColor = TextMuted;
        grid.CategorySplitterColor = BorderSoft;
        grid.HelpBackColor = Rail;
        grid.HelpForeColor = TextMuted;
        grid.HelpBorderColor = Border;
        grid.CommandsBackColor = Surface;
        grid.CommandsForeColor = Text;
        grid.DisabledItemForeColor = TextFaint;
        grid.SelectedItemWithFocusBackColor = AccentSoft;
        grid.SelectedItemWithFocusForeColor = Text;
        grid.Font = Ui;

        UseDarkScrollbars(grid);
    }

    private static void ApplyButton(Button button)
    {
        button.BackColor = SurfaceHigh;
        button.ForeColor = Text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Blend(SurfaceHigh, Text, 0.08);
        button.FlatAppearance.MouseDownBackColor = AccentSoft;
        button.UseVisualStyleBackColor = false;
        button.Font = Ui;
    }

    /// <summary>Somente as paginas: o <see cref="SegmentedTabControl"/> pinta o resto.</summary>
    private static void ApplyTabs(TabControl tabs)
    {
        tabs.BackColor = Canvas;
        tabs.ForeColor = Text;

        foreach (TabPage page in tabs.TabPages)
        {
            page.BackColor = Canvas;
            page.ForeColor = Text;
            page.UseVisualStyleBackColor = false;
        }
    }

    // --------------------------------------------------- barras de rolagem

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subApp, string? subId);

    /// <summary>
    /// Pede ao Windows as barras de rolagem escuras. Sem isto sobra uma faixa
    /// branca na lateral de toda grade e de todo log — o defeito mais visivel
    /// de um tema escuro feito pela metade.
    ///
    /// Nao ha garantia: em builds antigas do Windows a chamada e ignorada, e o
    /// pior caso e continuar como estava.
    /// </summary>
    public static void UseDarkScrollbars(Control control)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;

        Quando(control);

        // As barras do DataGridView sao controles filhos com janela propria: o
        // tema aplicado ao pai nao chega nelas, e elas so ganham handle quando
        // aparecem pela primeira vez.
        foreach (Control filho in control.Controls)
            if (filho is ScrollBar) Quando(filho);

        static void Quando(Control alvo)
        {
            if (alvo.IsHandleCreated) Aplicar(alvo);
            else alvo.HandleCreated += (_, _) => Aplicar(alvo);
        }

        static void Aplicar(Control alvo)
        {
            try { SetWindowTheme(alvo.Handle, "DarkMode_Explorer", null); }
            catch (DllNotFoundException) { /* Windows sem uxtheme */ }
        }
    }

    // ------------------------------------------------------ barra de titulo

    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Numero do atributo antes do Windows 10 20H1.</summary>
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    private const int SwpRedrawOnly = 0x0002 | 0x0001 | 0x0004 | 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Escurece a barra de titulo. Sem isto a janela fica com uma faixa clara
    /// em cima do conteudo escuro, que e pior que nao ter tema nenhum.
    /// </summary>
    public static void ApplyTitleBar(Form form)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;

        try
        {
            var enabled = 1;

            // O numero do atributo mudou no 20H1. Tenta o atual e cai para o
            // antigo se ele nao for aceito.
            if (DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));

            // O DWM so repinta a area nao-cliente no proximo evento de janela.
            // Sem este empurrao, a barra so escurece quando alguem a move.
            SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpRedrawOnly);
        }
        catch (DllNotFoundException)
        {
            // Windows sem dwmapi: a barra fica clara, e so.
        }
    }
}
