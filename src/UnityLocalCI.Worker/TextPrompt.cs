namespace UnityLocalCI.App;

/// <summary>
/// Caixa de uma pergunta so, no tema da janela.
///
/// Existe porque o nome do projeto deixou de ser editavel na grade: ele
/// identifica a fila, o arquivo de configuracao, o gatilho e o historico, e
/// muda-lo depois deixaria tudo isso orfao. Entao ele e perguntado uma vez, na
/// criacao.
/// </summary>
internal static class TextPrompt
{
    /// <summary>Devolve o texto informado, ou nulo se a pessoa desistiu.</summary>
    public static string? Ask(IWin32Window? owner, string titulo, string pergunta, string valorInicial = "")
    {
        using var janela = new Form
        {
            Text = titulo,
            Icon = AppIcon.Load(),
            Width = 460,
            Height = 210,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Canvas,
            Font = Theme.Ui,
            Padding = new Padding(18, 16, 18, 14),
        };

        var rotulo = new Label
        {
            Text = pergunta,
            Dock = DockStyle.Top,
            Height = 46,
            ForeColor = Theme.TextMuted,
        };

        var caixa = new TextBox { Dock = DockStyle.Top, Text = valorInicial, Height = 26 };

        var acoes = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Canvas,
        };

        var ok = new PillButton("Criar", ButtonKind.Primary) { Width = 110 };
        var cancelar = new PillButton("Cancelar", ButtonKind.Ghost) { Width = 110 };

        ok.Click += (_, _) => { janela.DialogResult = DialogResult.OK; janela.Close(); };
        cancelar.Click += (_, _) => janela.Close();

        acoes.Controls.AddRange([ok, cancelar]);

        janela.Controls.Add(caixa);
        janela.Controls.Add(rotulo);
        janela.Controls.Add(acoes);

        // Enter confirma, Esc desiste: numa caixa de um campo so, qualquer outra
        // coisa surpreende.
        janela.AcceptButton = ok;
        janela.CancelButton = cancelar;

        Theme.Apply(janela);
        rotulo.ForeColor = Theme.TextMuted;

        janela.Shown += (_, _) => { Theme.ApplyTitleBar(janela); caixa.SelectAll(); caixa.Focus(); };

        var resposta = owner is null ? janela.ShowDialog() : janela.ShowDialog(owner);

        return resposta == DialogResult.OK && caixa.Text.Trim().Length > 0 ? caixa.Text.Trim() : null;
    }
}
