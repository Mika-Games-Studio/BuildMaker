using System.Runtime.Versioning;

namespace UnityLocalCI.App;

/// <summary>
/// A faixa no topo da janela que oferece instalar o programa.
///
/// Ela so existe quando ha o que instalar. Aberto da pasta de Downloads, o
/// programa funciona inteiro — as builds rodam, a configuracao salva — e a
/// faixa aparece dizendo que ele ainda nao esta instalado. Depois de instalado,
/// a copia que roda e a de Programs\BuildMaker, e a faixa nao e nem criada.
///
/// Esse e o ponto do formato: nao ha uma tela de instalacao que impeca de usar
/// o programa antes de decidir. Quem quer so experimentar, experimenta; quem
/// quer que ele suba junto com o Windows e apareca no menu Iniciar, clica.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FaixaDeInstalacao : Panel, IPaintsItself
{
    /// <summary>
    /// O recado e desenhado no OnPaint, e nao posto num Label.
    ///
    /// Theme.Apply percorre os filhos de todo controle e reescreve o fundo de
    /// cada Label com BackdropOf, que so reconhece Card e NavRail — para
    /// qualquer outro pai ele devolve a cor do fundo da janela. Um Label aqui
    /// sairia com um retangulo preto por cima do verde da faixa.
    ///
    /// Dava para ensinar o BackdropOf a reconhecer esta faixa. Desenhar o texto
    /// resolve sem acrescentar caso especial ao tema — e e o que a NavRail ja faz
    /// com a assinatura do topo e a do rodape.
    /// </summary>
    private readonly string _recado;

    private readonly PillButton _instalar;

    private readonly PillButton _depois = new("Agora não", ButtonKind.Ghost)
    {
        Width = 96,
        Dock = DockStyle.Right,
        Backdrop = Theme.AccentSoft,
    };

    public FaixaDeInstalacao()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Dock = DockStyle.Top;
        Height = 52;
        BackColor = Theme.AccentSoft;
        Padding = new Padding(16, 10, 16, 10);

        // "Atualizar" quando ja existe uma copia instalada: e o mesmo botao e a
        // mesma acao, mas chamar de "Instalar" faria parecer que a instalacao
        // anterior sumiu.
        var jaTem = Instalacao.JaExisteInstalacao;
        _instalar = new PillButton(jaTem ? "Atualizar" : "Instalar", ButtonKind.Primary)
        {
            Width = 116,
            Dock = DockStyle.Right,
            Backdrop = Theme.AccentSoft,
        };

        _recado = jaTem
            ? "Esta cópia está rodando de fora da pasta de instalação."
            : $"O {AppNames.Display} ainda não está instalado nesta máquina.";

        _depois.Click += (_, _) => Visible = false;
        _instalar.Click += (_, _) => Instalar();

        // Ordem: Dock=Right empilha da borda para dentro, entao o ultimo
        // adicionado fica mais a esquerda.
        Controls.Add(_depois);
        Controls.Add(_instalar);
    }

    /// <summary>Anotado no log da janela, para o passo a passo ficar visivel.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string>? AoAnotar { get; init; }

    private void Instalar()
    {
        _instalar.Enabled = _depois.Enabled = false;
        _instalar.Text = "Instalando…";

        try
        {
            Instalacao.Instalar(passo => AoAnotar?.Invoke(passo));
        }
        catch (Exception excecao)
        {
            _instalar.Enabled = _depois.Enabled = true;
            _instalar.Text = "Instalar";

            MessageBox.Show(
                "Não foi possível instalar:" + Environment.NewLine + Environment.NewLine + excecao.Message,
                AppNames.Display, MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return;
        }

        // A copia instalada assume, e esta sai de cena.
        //
        // Duas copias do mesmo programa vivas ao mesmo tempo disputariam o banco
        // e os arquivos de gatilho — o SQLite aguenta, mas dois watchers
        // enfileirando a mesma build nao. Por isso e troca, e nao soma.
        if (Instalacao.AbrirCopiaInstalada())
        {
            Application.Exit();
            return;
        }

        MessageBox.Show(
            $"Instalado em {Instalacao.PastaDestino}." + Environment.NewLine + Environment.NewLine +
            "Abra o BuildMaker pelo atalho do menu Iniciar ou da área de trabalho.",
            AppNames.Display, MessageBoxButtons.OK, MessageBoxIcon.Information);

        Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var fundo = new SolidBrush(Theme.AccentSoft))
            g.FillRectangle(fundo, ClientRectangle);

        // Uma linha na base, e nao uma borda inteira: a faixa encosta no topo da
        // janela, e so o lado de baixo separa ela do conteudo.
        using (var linha = new Pen(Theme.AccentDeep))
            g.DrawLine(linha, 0, Height - 1, Width, Height - 1);

        // O texto termina onde os botoes comecam. Eles estao ancorados a
        // direita, entao a largura deles ja e a folga que precisa ser descontada
        // — somar as larguras a mao daria errado no dia em que um botao mudasse.
        var ocupadoPelosBotoes = _instalar.Width + _depois.Width + 16;
        var area = new Rectangle(
            Padding.Left,
            0,
            Math.Max(0, Width - Padding.Left - ocupadoPelosBotoes),
            Height - 1);

        TextRenderer.DrawText(
            g, _recado, Theme.Ui, area, Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}
