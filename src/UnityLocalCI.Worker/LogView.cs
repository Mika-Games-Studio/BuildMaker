using System.Runtime.InteropServices;

namespace UnityLocalCI.App;

/// <summary>O que a linha e, que decide a cor com que ela sai.</summary>
internal enum LogTone { Normal, Muted, Error, Warning }

/// <summary>
/// Uma linha de log ja dividida em colunas.
///
/// Hora, nivel e categoria sao curtos e alinhados; a mensagem e o que varia. E
/// o alinhamento que permite varrer o log com o olho em vez de ler linha por
/// linha — que e o que se faz com um log de servico aberto o dia inteiro.
/// </summary>
internal sealed record LogEntry(string Hora, string Nivel, string Categoria, string Mensagem, LogTone Tom)
{
    /// <summary>Linha de texto corrido, sem colunas. O log de build vem assim.</summary>
    public static LogEntry Corrida(string texto, LogTone tom)
        => new("", "", "", texto, tom);

    /// <summary>Como ela vai para a area de transferencia.</summary>
    public override string ToString()
        => string.Join("  ", new[] { Hora, Nivel, Categoria, Mensagem }.Where(p => p.Length > 0));
}

/// <summary>
/// A area de log da janela.
///
/// E uma ListBox e nao uma caixa de texto: com colunas em cores diferentes na
/// mesma linha, uma caixa de texto simples nao serve, e uma RichTextBox recebe
/// o texto inteiro a cada linha nova. Aqui cada linha e um item, o que tambem
/// da rolagem virtual de graca — um log de dez mil linhas nao pesa mais que um
/// de dez.
///
/// O que se perde da caixa de texto e selecionar com o mouse; em troca,
/// Ctrl+C copia as linhas selecionadas, que e o que se faz com log.
/// </summary>
internal sealed class LogView : ListBox, IPaintsItself
{
    /// <summary>Recuos das colunas, em caracteres do proprio monoespacado.</summary>
    private const int ColunaNivel = 10;
    private const int ColunaCategoria = 15;
    private const int ColunaMensagem = 28;

    private readonly int _celula;

    /// <summary>
    /// Mais recente em cima.
    ///
    /// Vale para o log do servico, que nao tem fim: com a linha nova no topo,
    /// o que acabou de acontecer esta sempre visivel sem rolar nada. Nao vale
    /// para o log de uma build, que e uma narrativa com comeco e fim — lida de
    /// tras para a frente, ela nao faz sentido.
    /// </summary>
    private readonly bool _novoNoTopo;

    public LogView(bool novoNoTopo = false)
    {
        _novoNoTopo = novoNoTopo;

        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        SelectionMode = SelectionMode.MultiExtended;
        IntegralHeight = false;
        HorizontalScrollbar = true;

        BackColor = Theme.LogSurface;
        ForeColor = Theme.Text;
        Font = Theme.Mono;
        ItemHeight = 19;

        // A largura de um caractere do monoespacado: e por ela que as colunas
        // sao posicionadas, para acompanharem o DPI da maquina.
        _celula = TextRenderer.MeasureText("0000000000", Font).Width / 10;

        Theme.UseDarkScrollbars(this);
    }

    /// <summary>Teto de linhas. Acima dele as mais antigas saem pela frente.</summary>
    private const int Capacidade = 5000;

    public void Anexar(LogEntry linha)
    {
        BeginUpdate();
        try
        {
            if (_novoNoTopo)
            {
                Items.Insert(0, linha);
                while (Items.Count > Capacidade) Items.RemoveAt(Items.Count - 1);
            }
            else
            {
                Items.Add(linha);
                while (Items.Count > Capacidade) Items.RemoveAt(0);
            }
        }
        finally { EndUpdate(); }

        Acompanhar();
    }

    public void Preencher(IEnumerable<LogEntry> linhas)
    {
        var lista = linhas.ToList();
        if (_novoNoTopo) lista.Reverse();

        BeginUpdate();
        try
        {
            Items.Clear();
            foreach (var linha in lista) Items.Add(linha);
            while (Items.Count > Capacidade) Items.RemoveAt(_novoNoTopo ? Items.Count - 1 : 0);
        }
        finally { EndUpdate(); }

        Acompanhar();
    }

    public void Limpar()
    {
        Items.Clear();
        HorizontalExtent = 0;
    }

    /// <summary>
    /// Vai para onde esta a linha nova. Num log ao vivo o que interessa e a
    /// ultima que chegou, nao a primeira que chegou um dia.
    /// </summary>
    private void Acompanhar()
    {
        if (Items.Count == 0) return;
        TopIndex = _novoNoTopo ? 0 : Items.Count - 1;
    }

    /// <summary>
    /// Copia o log para a area de transferencia: a selecao, quando ha uma, e o
    /// log inteiro quando nao ha.
    ///
    /// As duas coisas pelo mesmo caminho porque sao a mesma intencao. Quem
    /// selecionou quer aquele trecho; quem nao selecionou nada e clicou em
    /// Copiar quer tudo — e "tudo" e o caso comum, que e mandar o log para
    /// alguem olhar.
    /// </summary>
    /// <returns>Quantas linhas foram copiadas; zero se nao havia nada ou se a
    /// area de transferencia recusou.</returns>
    public int Copiar()
    {
        var origem = SelectedItems.Count > 0 ? SelectedItems.Cast<object>() : Items.Cast<object>();
        var linhas = origem.Select(i => i.ToString()).ToArray();

        if (linhas.Length == 0) return 0;

        // A area de transferencia falha quando outro programa a esta segurando.
        // Perder uma copia nao pode derrubar a janela.
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, linhas));
            return linhas.Length;
        }
        catch (ExternalException)
        {
            return 0;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            Copiar();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        if (Items[e.Index] is not LogEntry linha) return;

        var g = e.Graphics;
        var selecionada = (e.State & DrawItemState.Selected) != 0;

        using (var fundo = new SolidBrush(selecionada ? Theme.AccentSoft : Theme.LogSurface))
            g.FillRectangle(fundo, e.Bounds);

        var (rotulo, mensagem) = Cores(linha.Tom);

        // Quando nao ha coluna de nivel — o log de build nao tem —, quem carrega
        // a cor do estado e a etapa. Assim toda linha de erro tem um rotulo
        // colorido, e nao so a mensagem.
        var corDaEtapa = linha.Nivel.Length == 0 ? rotulo : Theme.TextMuted;

        Coluna(g, e.Bounds, linha.Hora, 1, Theme.TextFaint);
        Coluna(g, e.Bounds, linha.Nivel, ColunaNivel, rotulo);
        Coluna(g, e.Bounds, linha.Categoria, ColunaCategoria, corDaEtapa);

        var recuo = linha.Hora.Length == 0 ? 1 : ColunaMensagem;
        Coluna(g, e.Bounds, linha.Mensagem, recuo, mensagem);
    }

    private void Coluna(Graphics g, Rectangle bounds, string texto, int recuoEmCaracteres, Color cor)
    {
        if (texto.Length == 0) return;

        var x = bounds.X + recuoEmCaracteres * _celula;

        TextRenderer.DrawText(g, texto, Font,
            new Rectangle(x, bounds.Y, bounds.Right - x, bounds.Height), cor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
            TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// O rotulo leva a cor cheia e a mensagem leva a clara. Uma frase inteira
    /// no vermelho forte cansa, e e a frase de erro que precisa ser lida ate o
    /// fim.
    /// </summary>
    private static (Color Rotulo, Color Mensagem) Cores(LogTone tom) => tom switch
    {
        LogTone.Error => (Theme.Danger, Theme.DangerSoft),
        LogTone.Warning => (Theme.Warning, Theme.WarningSoft),
        LogTone.Muted => (Theme.TextFaint, Theme.TextFaint),
        _ => (Theme.TextFaint, Theme.Text),
    };

    /// <summary>
    /// A ListBox so mostra a barra horizontal ate onde mandarem: sem isto, a
    /// mensagem longa fica cortada e nao ha como rolar ate ela.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AjustarLargura();
    }

    private void AjustarLargura()
    {
        var maior = 0;

        foreach (var item in Items)
            if (item is LogEntry linha)
                maior = Math.Max(maior, (ColunaMensagem + linha.Mensagem.Length) * _celula);

        HorizontalExtent = maior;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        AjustarLargura();
    }
}
