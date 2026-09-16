using System.Runtime.InteropServices;

namespace UnityLocalCI.App;

/// <summary>
/// Tema escuro da janela.
///
/// O WinForms nao tem tema: cada controle pinta com as cores do sistema. Entao
/// o tema e aplicado explicitamente, controle a controle, e alguns precisam de
/// tratamento proprio — o DataGridView ignora as cores se EnableHeadersVisualStyles
/// ficar ligado, o TabControl desenha as abas pelo sistema, e a barra de titulo
/// so escurece por uma chamada ao DWM.
/// </summary>
public static class Theme
{
    // Fundo da janela e das abas.
    public static readonly Color Background = Color.FromArgb(0x1E, 0x1E, 0x1E);

    // Superficies que precisam se destacar do fundo: grades, caixas de texto.
    public static readonly Color Surface = Color.FromArgb(0x25, 0x25, 0x26);

    // Cabecalhos de coluna e abas nao selecionadas.
    public static readonly Color Elevated = Color.FromArgb(0x2D, 0x2D, 0x30);

    public static readonly Color Border = Color.FromArgb(0x3F, 0x3F, 0x46);
    public static readonly Color Text = Color.FromArgb(0xE6, 0xE6, 0xE6);
    public static readonly Color TextMuted = Color.FromArgb(0x9A, 0x9A, 0x9A);

    public static readonly Color Selection = Color.FromArgb(0x0E, 0x63, 0x9C);
    public static readonly Color SelectionText = Color.White;

    /// <summary>
    /// Vermelho claro, e nao Firebrick: o vermelho escuro some no fundo escuro,
    /// e falha e justamente o que precisa saltar aos olhos.
    /// </summary>
    public static readonly Color Danger = Color.FromArgb(0xF4, 0x87, 0x71);

    public static readonly Color Success = Color.FromArgb(0x89, 0xD1, 0x85);

    public static void Apply(Control root)
    {
        ApplyTo(root);

        foreach (Control child in root.Controls)
            Apply(child);
    }

    private static void ApplyTo(Control control)
    {
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
                textBox.BorderStyle = BorderStyle.FixedSingle;
                return;

            case ListBox list:
                list.BackColor = Surface;
                list.ForeColor = Text;
                list.BorderStyle = BorderStyle.FixedSingle;
                return;

            case SplitContainer split:
                split.BackColor = Border;
                split.Panel1.BackColor = Background;
                split.Panel2.BackColor = Background;
                return;

            default:
                control.BackColor = Background;
                control.ForeColor = Text;
                return;
        }
    }

    private static void ApplyGrid(DataGridView grid)
    {
        // Sem isto o Windows pinta o cabecalho com o tema visual e ignora as
        // cores definidas abaixo.
        grid.EnableHeadersVisualStyles = false;

        grid.BackgroundColor = Surface;
        grid.GridColor = Border;
        grid.BorderStyle = BorderStyle.None;
        grid.ForeColor = Text;

        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = Selection;
        grid.DefaultCellStyle.SelectionForeColor = SelectionText;

        grid.ColumnHeadersDefaultCellStyle.BackColor = Elevated;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Elevated;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;

        grid.RowHeadersDefaultCellStyle.BackColor = Elevated;
        grid.RowHeadersDefaultCellStyle.ForeColor = Text;
    }

    private static void ApplyPropertyGrid(PropertyGrid grid)
    {
        grid.BackColor = Background;
        grid.ViewBackColor = Surface;
        grid.ViewForeColor = Text;
        grid.ViewBorderColor = Border;
        grid.LineColor = Border;
        grid.CategoryForeColor = Text;
        grid.CategorySplitterColor = Border;
        grid.HelpBackColor = Elevated;
        grid.HelpForeColor = TextMuted;
        grid.HelpBorderColor = Border;
        grid.CommandsBackColor = Background;
        grid.CommandsForeColor = Text;
        grid.DisabledItemForeColor = TextMuted;
        grid.SelectedItemWithFocusBackColor = Selection;
        grid.SelectedItemWithFocusForeColor = SelectionText;
    }

    private static void ApplyButton(Button button)
    {
        button.BackColor = Elevated;
        button.ForeColor = Text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x3A, 0x3A, 0x3D);
        button.FlatAppearance.MouseDownBackColor = Selection;
        button.UseVisualStyleBackColor = false;
    }

    /// <summary>Somente as paginas: o DarkTabControl pinta as abas e a faixa.</summary>
    private static void ApplyTabs(TabControl tabs)
    {
        tabs.BackColor = Background;
        tabs.ForeColor = Text;

        foreach (TabPage page in tabs.TabPages)
        {
            page.BackColor = Background;
            page.ForeColor = Text;
            page.UseVisualStyleBackColor = false;
        }
    }

    // ------------------------------------------------------------ barra de titulo

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
