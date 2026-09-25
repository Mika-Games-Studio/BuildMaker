namespace UnityLocalCI.App;

/// <summary>
/// Tres caixas lado a lado, cada uma com um numero grande e a frase que diz o
/// que ele conta.
///
/// Existe porque "conectado" nao responde a pergunta que importa — se os
/// projetos vao conseguir clonar. Quantos herdam esta conexao, quantos tem
/// credencial propria e o que o token pode fazer respondem. Em frase corrida
/// esses tres numeros se perdem no meio do texto; em caixa, leem-se de relance.
/// </summary>
internal sealed class StatRow : Control, IPaintsItself
{
    private const int Vao = 12;

    private (string Valor, string Rotulo)[] _caixas = [];

    public StatRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Height = 66;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
    }

    public void Mostrar(params (string Valor, string Rotulo)[] caixas)
    {
        _caixas = caixas;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var fundo = new SolidBrush(Parent?.BackColor ?? Theme.Surface))
            g.FillRectangle(fundo, ClientRectangle);

        if (_caixas.Length == 0) return;

        var largura = (Width - Vao * (_caixas.Length - 1)) / _caixas.Length;
        if (largura < 60) return;

        for (var i = 0; i < _caixas.Length; i++)
        {
            var area = new Rectangle(i * (largura + Vao), 0, largura - 1, Height - 1);

            Theme.FillRounded(g, area, Theme.SurfaceHigh, 10f);
            Theme.DrawRounded(g, area, Theme.BorderSoft, 10f);

            var (valor, rotulo) = _caixas[i];

            // O numero em corpo de titulo e o rotulo apagado embaixo: e a
            // hierarquia que faz a caixa ser lida como um dado so.
            UiKit.Text(g, valor, Theme.Title,
                new Rectangle(area.X + 14, area.Y + 10, area.Width - 20, 24),
                Theme.Text, UiKit.LeftMiddle);

            UiKit.Text(g, rotulo, Theme.UiSmall,
                new Rectangle(area.X + 14, area.Y + 36, area.Width - 20, 18),
                Theme.TextFaint, UiKit.LeftMiddle);
        }
    }
}
