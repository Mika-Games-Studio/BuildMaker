using System.Runtime.InteropServices;

namespace UnityLocalCI.App;

/// <summary>
/// O tutorial, dentro do proprio programa.
///
/// Fica aqui e nao so no README porque quem instala o app na maquina de build
/// nem sempre e quem clonou o repositorio — e a primeira duvida ("gravei o PAT
/// onde?") aparece justamente com a janela aberta na frente.
///
/// O texto e montado em codigo, e nao lido de um arquivo, para nao existir a
/// possibilidade de o executavel rodar sem o tutorial junto.
/// </summary>
internal sealed class TutorialPage : Panel
{
    private readonly TableLayoutPanel _pilha;
    private readonly List<Label> _paragrafos = [];

    public TutorialPage()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Canvas;
        AutoScroll = true;
        Padding = new Padding(0, 0, 16, 16);

        _pilha = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Canvas,
        };
        _pilha.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Controls.Add(_pilha);

        Montar();

        // A largura util so existe depois que o painel recebe tamanho; sem isto
        // os paragrafos nascem numa linha so.
        Resize += (_, _) => Reajustar();
        Reajustar();
    }

    private int LarguraUtil => Math.Max(320, ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);

    private int _larguraAplicada = -1;

    /// <summary>
    /// Reaplica a largura dos paragrafos, e so quando ela mudou de verdade.
    ///
    /// Mexer no MaximumSize dispara layout, o layout pode mudar a altura, a
    /// altura pode fazer a barra de rolagem aparecer ou sumir, e isso muda a
    /// largura util — que dispara tudo de novo. Com a guarda, a segunda volta
    /// nao acontece; sem ela, o laco roda para sempre a 100% de um nucleo, com
    /// a janela viva mas inutil.
    /// </summary>
    private void Reajustar()
    {
        var largura = LarguraUtil;
        if (largura == _larguraAplicada) return;

        _larguraAplicada = largura;

        SuspendLayout();
        try
        {
            _pilha.MaximumSize = new Size(largura, 0);

            foreach (var paragrafo in _paragrafos)
                paragrafo.MaximumSize = new Size(largura - paragrafo.Margin.Horizontal, 0);
        }
        finally { ResumeLayout(performLayout: true); }
    }

    // ------------------------------------------------------------- conteudo

    private void Montar()
    {
        Secao("O que este programa faz");
        Paragrafo(
            "Ele observa a branch de homologação dos seus projetos Unity. Quando aparece um commit novo, " +
            "ele clona ou atualiza o projeto, roda o build pelo Unity, compacta o resultado e copia o zip " +
            "para a pasta que o time combinou. Tudo nesta máquina: sem nuvem, sem painel web.");
        Paragrafo(
            "A pasta de destino é a interface para quem só quer o artefato. Quem quiser detalhe abre esta janela.");

        Secao("Preparar a máquina");

        Passo(1, "Instalar o programa",
            "Gere o pacote uma vez no repositório e rode o instalador na máquina de build. Ele não pede " +
            "administrador: instala na sua pasta de usuário, cria os atalhos e liga o início automático.");
        Comando(@"powershell -ExecutionPolicy Bypass -File tools\publicar.ps1");
        Comando(@"powershell -ExecutionPolicy Bypass -File tools\instalar.ps1");

        Passo(2, "Conectar ao GitHub",
            "Na página Configuração, aba GitHub, clique em \"Conectar ao GitHub\". Ele procura primeiro uma " +
            "conta que esta máquina já tenha — a do cofre, a que o Git usa, a do GitHub CLI — e só abre o " +
            "navegador se não achar nenhuma. A conexão é da máquina inteira: todos os projetos herdam.");
        Lista(
            "conectado, a aba mostra a foto e o @ da conta — é a confirmação de que é a conta certa",
            "o token vai para o Gerenciador de Credenciais do Windows; a configuração guarda só o NOME da credencial",
            "\"Entrar com outra conta\" é o caminho para trocar de conta ou para uma máquina limpa");
        Paragrafo("Se preferir gravar um token à mão, o nome da credencial é este:", recuo: 34);
        Comando("cmdkey /generic:UnityLocalCI_GitHub /user:pat /pass:SEU_PAT_AQUI");

        Passo(3, "Ativar a licença do Unity",
            "Uma vez por máquina. A licença fica com escopo de máquina, então funciona também quando o CI " +
            "roda como serviço, sem ninguém logado.");
        Comando("unity license activate --serial <SERIAL> --username <USUARIO> --password <SENHA>");

        Secao("Cadastrar um projeto");

        Passo(4, "Vincular o projeto Unity",
            "Na página Configuração, aba Projetos, use \"Vincular projeto Unity\". Escolha a pasta do projeto " +
            "que você já tem na máquina — a que tem Assets e ProjectSettings dentro.");
        Lista(
            "a URL e a branch vêm do próprio clone que você apontou",
            "a versão do editor vem do ProjectSettings\\ProjectVersion.txt do projeto, então ela nunca é adivinhada",
            "o projeto entra desligado, para você conferir antes de valer");
        Aviso(
            "O CI nunca constrói dentro da pasta que você escolheu. Ele clona o repositório no workspace dele, " +
            "que é exclusivo e onde ele apaga o que não for rastreado. Apontar o workspace para a sua pasta de " +
            "trabalho faria você perder o que não estivesse commitado.");

        Passo(5, "Escolher as pastas",
            "Os campos de caminho abrem a caixa do Windows: clique no campo e depois no botão com as reticências. " +
            "O que precisa ser preenchido é a pasta de destino, onde o time vai pegar o zip.");

        Passo(6, "Salvar e reiniciar",
            "Gravar passa pela mesma validação que o serviço usa ao iniciar, então não dá para salvar algo que " +
            "derrubaria o próximo start. Quando estiver certo, marque Enabled como True e salve de novo.");

        Secao("Como uma build dispara");
        Lista(
            "commit novo na branch observada — o padrão; uma rajada de commits vira uma build só, do último",
            "botão Construir agora, na página Projetos",
            "tocar o arquivo de gatilho do projeto, útil para chamar de um script",
            "hook post-merge do Git, que avisa esta máquina na hora do merge");
        Paragrafo(
            "Duas builds do mesmo projeto nunca rodam juntas: o Unity tranca a pasta Library. Projetos " +
            "diferentes rodam em paralelo até o limite da fila, que também respeita a RAM livre.");

        Secao("Onde o time pega o resultado");
        Paragrafo(
            "Na pasta de destino de cada projeto ficam os zips das builds, com data, hora e commit no nome — " +
            "e nada mais. Cada zip contém exatamente o que o Unity produziu. Nenhum arquivo do CI entra ali: " +
            "o zip é o que vai para o navegador, para a loja ou para quem pediu a build, e arquivo estranho " +
            "no meio confunde quem recebe e derruba a validação de um portal.");
        Paragrafo(
            "O status de cada projeto e o histórico ficam em JSON, em %LOCALAPPDATA%\\BuildMaker\\status.");
        Aviso(
            "WebGL não roda abrindo o index.html direto: o navegador bloqueia .wasm e .data por file://, e com " +
            "compressão ainda falta o cabeçalho de codificação. Dá tela preta sem erro nenhum. Para testar, " +
            "descompacte o zip e aponte um servidor local para a pasta.");

        Secao("Quando alguma coisa falha");
        Paragrafo(
            "A página Builds mostra o histórico e o log completo da build selecionada. O resumo do erro sai " +
            "na própria linha, e no log os erros que o build script marcou aparecem com o prefixo [UnityLocalCI]. " +
            "Erro de compilação aparece com arquivo, linha e código.");

        Secao("Fechar, bandeja e início automático");
        Paragrafo(
            "Fechar no X esconde a janela na bandeja e as builds continuam. Clicar no ícone traz a janela de " +
            "volta; o botão direito tem Abrir, Iniciar com o Windows e Sair. Sair encerra de verdade, e avisa " +
            "que nenhuma build será disparada enquanto o programa estiver fechado.");
        Aviso(
            "Início automático não é serviço. O atalho abre o programa quando VOCÊ faz login. Para o CI rodar " +
            "com a máquina ligada e ninguém logado, registre-o como serviço do Windows com tools\\install-service.ps1.");

        Secao("O que ainda não foi provado");
        Paragrafo(
            "Sendo franco sobre o estado da ferramenta: o pipeline, a janela e o instalador foram exercitados de " +
            "verdade, mas o Builder.cs nunca foi compilado por um editor Unity e nenhuma build WebGL real rodou " +
            "até aqui. A primeira build de um projeto seu é que vai dizer.");
    }

    // ------------------------------------------------------------ elementos

    private void Secao(string texto)
    {
        Adicionar(new Label
        {
            Text = texto,
            AutoSize = true,
            Font = Theme.Title,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 26, 0, 10),
        });
    }

    private void Passo(int numero, string titulo, string texto)
    {
        Adicionar(new StepHeader(numero, titulo) { Margin = new Padding(0, 14, 0, 6) });
        Paragrafo(texto, recuo: 34);
    }

    private void Paragrafo(string texto, int recuo = 0)
    {
        var label = new Label
        {
            Text = texto,
            AutoSize = true,
            Font = Theme.Ui,
            ForeColor = Theme.TextMuted,
            Margin = new Padding(recuo, 0, 0, 8),
        };

        _paragrafos.Add(label);
        Adicionar(label);
    }

    private void Lista(params string[] itens)
    {
        foreach (var item in itens)
            Paragrafo("•   " + item, recuo: 34);
    }

    private void Comando(string texto)
        => Adicionar(new CommandBlock(texto) { Margin = new Padding(34, 2, 0, 8) });

    private void Aviso(string texto)
    {
        var label = new Label
        {
            Text = texto,
            AutoSize = true,
            Font = Theme.Ui,
            ForeColor = Theme.Warning,
            Margin = new Padding(34, 4, 0, 10),
            Padding = new Padding(12, 8, 8, 8),
            BackColor = Theme.Blend(Theme.Canvas, Theme.Warning, 0.06),
        };

        _paragrafos.Add(label);
        Adicionar(label);
    }

    private void Adicionar(Control control)
    {
        control.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        _pilha.Controls.Add(control);
    }
}

/// <summary>Numero do passo num circulo, com o titulo ao lado.</summary>
internal sealed class StepHeader : Control, IPaintsItself
{
    private readonly int _numero;

    public StepHeader(int numero, string titulo)
    {
        _numero = numero;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Text = titulo;
        Font = Theme.UiBold;
        Height = 26;
        Width = 520;
        BackColor = Theme.Canvas;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var fundo = new SolidBrush(Theme.Canvas))
            g.FillRectangle(fundo, ClientRectangle);

        Theme.FillRounded(g, new Rectangle(0, 2, 22, 22), Theme.AccentSoft, 11f);

        UiKit.Text(g, _numero.ToString(), Theme.UiSmallBold, new Rectangle(0, 2, 22, 22),
            Theme.AccentHover, UiKit.Centered);

        UiKit.Text(g, Text, Theme.UiBold, new Rectangle(34, 0, Width - 34, Height),
            Theme.Text, UiKit.LeftMiddle);
    }
}

/// <summary>
/// Uma linha de comando, em fonte monoespacada, que se copia com um clique.
/// Comando de tutorial existe para ser colado; deixar o usuario selecionar
/// texto a mao num rotulo seria pedir erro de digitação.
/// </summary>
internal sealed class CommandBlock : Control, IPaintsItself
{
    private readonly string _comando;
    private readonly System.Windows.Forms.Timer _voltar = new() { Interval = 1400 };

    private bool _hover;
    private bool _copiado;

    public CommandBlock(string comando)
    {
        _comando = comando;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Height = 36;
        Width = 720;
        Cursor = Cursors.Hand;
        BackColor = Theme.Canvas;

        _voltar.Tick += (_, _) => { _voltar.Stop(); _copiado = false; Invalidate(); };
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        try
        {
            // Com numero de tentativas explicito: outra aplicacao pode estar
            // segurando a area de transferencia, e o padrao do WinForms insiste
            // por cerca de um segundo com a janela parada.
            Clipboard.SetDataObject(_comando, copy: true, retryTimes: 4, retryDelay: 40);

            _copiado = true;
            _voltar.Stop();
            _voltar.Start();
        }
        catch (ExternalException)
        {
            // Area de transferencia ocupada: sem aviso, que um erro modal por
            // causa de um copiar seria pior que o copiar falho.
        }

        Invalidate();
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var fundo = new SolidBrush(Theme.Canvas))
            g.FillRectangle(fundo, ClientRectangle);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

        Theme.FillRounded(g, bounds, _hover ? Theme.SurfaceHigh : Theme.Surface, 8f);
        Theme.DrawRounded(g, bounds, Theme.Border, 8f);

        var etiqueta = _copiado ? "copiado" : _hover ? "clique para copiar" : "";
        var largura = etiqueta.Length == 0 ? 0 : TextRenderer.MeasureText(etiqueta, Theme.UiSmall).Width + 12;

        UiKit.Text(g, _comando, Theme.Mono, new Rectangle(12, 0, Width - largura - 18, Height),
            Theme.Blend(Theme.Text, Theme.TextMuted, 0.25), UiKit.LeftMiddle);

        if (largura > 0)
            UiKit.Text(g, etiqueta, Theme.UiSmall, new Rectangle(Width - largura, 0, largura - 6, Height),
                _copiado ? Theme.Success : Theme.TextFaint, UiKit.LeftMiddle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _voltar.Dispose();
        base.Dispose(disposing);
    }
}
