using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Hosting;
using UnityLocalCI.Core.Pipeline;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.Queue;
using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.State;
using UnityLocalCI.Core.Unity;
using UnityLocalCI.Core.Watching;

namespace UnityLocalCI.App;

/// <summary>
/// A janela do UnityLocalCI. Quatro paginas: projetos, builds, log e configuracao.
///
/// Ela nao guarda estado proprio: tudo que mostra vem do mesmo SQLite que o
/// servico escreve. Assim a janela nunca discorda do que aconteceu de verdade,
/// e fecha-la nao perde nada.
///
/// A navegacao e uma coluna a esquerda, e cada pagina tem cabecalho com as
/// proprias acoes. Abas no topo com uma barra de botoes embaixo gastavam duas
/// faixas de altura para dizer menos.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private readonly HostController _controller;
    private readonly LiveLog _liveLog;
    private readonly string _configPath;
    private readonly System.Windows.Forms.Timer _refresh = new();

    private readonly DataGridView _projectsGrid = NewGrid();
    private readonly DataGridView _buildsGrid = NewGrid();
    private readonly LogView _buildLog = new() { Dock = DockStyle.Fill };

    /// <summary>De qual build e o log do painel de baixo.</summary>
    private readonly Label _buildLogTitle = new()
    {
        Dock = DockStyle.Top,
        AutoSize = false,
        Height = 26,
        Font = Theme.UiBold,
        ForeColor = Theme.TextMuted,
        BackColor = Theme.Surface,
        Padding = new Padding(2, 4, 2, 6),
        Text = "Log da build",
    };
    private readonly LogView _serviceLog = new(novoNoTopo: true) { Dock = DockStyle.Fill };
    private readonly ConfigPanel _configPanel;

    private readonly StatusBar _status = new();
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
    private readonly List<Panel> _pages = [];

    private readonly PillButton _buildNow = new("Construir agora", ButtonKind.Primary) { Width = 136 };
    private readonly PillButton _openFolder = new("Abrir pasta") { Width = 104 };
    private readonly PillButton _republish = new("Reenviar pendentes", ButtonKind.Ghost) { Width = 148 };
    private readonly PillButton _toggleHost = new("Parar serviço", ButtonKind.Ghost) { Width = 116 };

    private readonly PillButton _cancelarBuild = new("Cancelar build") { Width = 130 };
    private readonly PillButton _limparHistorico = new("Limpar histórico", ButtonKind.Ghost) { Width = 142 };
    private readonly PillButton _pausarFila = new("Pausar fila", ButtonKind.Ghost) { Width = 116 };

    /// <summary>Gira a roda das builds em execucao. Parado quando nao ha nenhuma.</summary>
    private readonly System.Windows.Forms.Timer _spinner = new();
    private float _anguloDaRoda;

    /// <summary>Indices das colunas da grade de builds que o codigo precisa nomear.</summary>
    private const int ColunaDaRoda = 0;
    private const int ColunaDoId = 1;
    private const int ColunaDoProjeto = 2;
    private const int ColunaDoResultado = 3;
    private const int ColunaDoErro = 8;
    private const int ColunaDaAcao = 9;

    private long? _selectedBuildId;

    /// <summary>
    /// Atribuida logo apos a construcao: a bandeja precisa da janela e a janela
    /// dela. Campo e nao propriedade, para o analisador do WinForms nao pedir
    /// atributos de serializacao de designer num membro que nunca vai ao designer.
    /// </summary>
    internal TrayPresence? Tray;

    /// <summary>Marcado pela bandeja antes de sair, para o fechamento nao ser escondido.</summary>
    internal bool ExitRequested;

    public MainForm(HostController controller, LiveLog liveLog, string configPath)
    {
        _controller = controller;
        _liveLog = liveLog;
        _configPath = configPath;
        _configPanel = new ConfigPanel(configPath, controller);

        Text = AppNames.Display;
        Icon = AppIcon.Load();
        Width = 1180;
        Height = 740;
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui;

        BuildLayout();

        _controller.StateChanged += OnStateChanged;
        _liveLog.LineAdded += OnLogLine;

        _refresh.Interval = (int)RefreshInterval.TotalMilliseconds;
        _refresh.Tick += (_, _) => RefreshData();
        _refresh.Start();

        Theme.Apply(this);

        LoadServiceLog();
        RefreshData();
        UpdateStatus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // De novo depois de exibida: em algumas versoes do Windows o DWM ignora
        // o atributo enquanto a janela ainda nao apareceu.
        Theme.ApplyTitleBar(this);
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        _pages.Add(BuildProjectsPage());
        _pages.Add(BuildBuildsPage());
        _pages.Add(BuildServiceLogPage());
        _pages.Add(BuildTutorialPage());
        _pages.Add(BuildConfigPage());

        foreach (var page in _pages)
        {
            page.Visible = false;
            _pageHost.Controls.Add(page);
        }

        _pages[0].Visible = true;

        var conteudo = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(20, 2, 20, 10),
        };
        conteudo.Controls.Add(_pageHost);

        var rail = new NavRail
        {
            Dock = DockStyle.Left,
            HeaderMark = AppIcon.LoadMark(),
            HeaderTitle = AppNames.MarkStrong,
            HeaderTitleTail = AppNames.MarkSoft,
            HeaderSubtitle = AppNames.Descriptor,
        };

        rail.AddItem("Projetos", NavGlyph.Projects);
        rail.AddItem("Builds", NavGlyph.Builds);
        rail.AddItem("Log do serviço", NavGlyph.Log);
        rail.AddItem("Tutorial", NavGlyph.Tutorial);
        rail.AddItem("Configuração", NavGlyph.Settings);
        rail.SelectionChanged += ShowPage;

        // Ordem importa: o ultimo adicionado e posicionado primeiro e fica com a
        // borda externa. A coluna precisa da altura inteira, entao entra por ultimo.
        Controls.Add(conteudo);
        Controls.Add(_status);
        Controls.Add(rail);
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < _pages.Count; i++)
            _pages[i].Visible = i == index;
    }

    /// <summary>
    /// Monta a pagina. O corpo entra antes do cabecalho de proposito: o
    /// WinForms posiciona os filhos do ultimo para o primeiro, entao quem entra
    /// depois e que fica com a borda.
    /// </summary>
    private static Panel NewPage(PageHeader header, Control body)
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
        body.Dock = DockStyle.Fill;

        page.Controls.Add(body);
        page.Controls.Add(header);

        return page;
    }

    private Panel BuildProjectsPage()
    {
        var header = new PageHeader(
            "Projetos",
            "O que o serviço observa e como terminou a última build de cada um.");

        _buildNow.Click += (_, _) => BuildSelectedProject();
        _openFolder.Click += (_, _) => OpenSelectedFolder();
        _republish.Click += (_, _) => RepublishPending();
        _toggleHost.Click += (_, _) => ToggleHost();

        // Fluxo da direita para a esquerda: o primeiro adicionado fica na ponta
        // direita, que e onde se procura a acao principal.
        header.Actions.Controls.AddRange([_buildNow, _openFolder, _republish, _toggleHost]);

        _projectsGrid.Columns.AddRange(
            TextColumn("Projeto", 150),
            TextColumn("Estado", 110),
            TextColumn("Última build", 130),
            MonoColumn("Commit", 90),

            // Mais larga que a da pagina de builds: enquanto a build corre esta
            // coluna carrega a etapa junto com o tempo.
            TextColumn("Duração", 150),
            TextColumn("Artefato", 300));

        PaintStatusColumn(_projectsGrid, statusColumn: 1);
        StretchLastColumn(_projectsGrid);

        return NewPage(header, NewCard(_projectsGrid));
    }

    private Panel BuildBuildsPage()
    {
        var header = new PageHeader(
            "Builds",
            "Histórico das execuções. Selecione uma linha para ler o log dela.");

        _limparHistorico.Click += (_, _) => ClearHistory();
        _cancelarBuild.Click += (_, _) => CancelRunningBuild();
        _pausarFila.Click += (_, _) => TogglePause();

        header.Actions.Controls.AddRange([_cancelarBuild, _limparHistorico, _pausarFila]);

        _buildsGrid.Columns.AddRange(
            // Sem rotulo e estreita: e so onde a roda gira enquanto a build
            // corre. Uma coluna com titulo pediria uma explicacao que a propria
            // animacao ja da.
            TextColumn("", 34),
            TextColumn("#", 56),
            TextColumn("Projeto", 120),
            TextColumn("Resultado", 110),
            TextColumn("Quando", 110),
            TextColumn("Duração", 90),
            MonoColumn("Commit", 90),
            TextColumn("Autor", 130),
            TextColumn("Erro", 320),
            TextColumn("", 44));

        PaintStatusColumn(_buildsGrid, statusColumn: ColunaDoResultado);
        PaintSpinnerColumn(_buildsGrid);
        PaintActionColumn(_buildsGrid);

        // A coluna larga e a do erro, e nao a ultima: a ultima agora e a dos
        // botoes de cada linha, e ela tem largura fixa.
        _buildsGrid.Columns[ColunaDoErro].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _buildsGrid.Columns[ColunaDoErro].MinimumWidth = 110;

        _buildsGrid.SelectionChanged += (_, _) => ShowSelectedBuildLog();
        _buildsGrid.CellClick += (_, e) => OnBuildsGridClick(e);

        // A roda so gira quando ha o que girar: sem build em execucao o timer
        // fica parado, e uma janela aberta o dia inteiro nao repinta a toa.
        _spinner.Interval = 90;
        _spinner.Tick += (_, _) =>
        {
            _anguloDaRoda = (_anguloDaRoda + 24f) % 360f;
            InvalidateSpinnerCells();
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 10,
            BackColor = Theme.Canvas,
        };

        split.Panel1.Controls.Add(NewCard(_buildsGrid));
        var cartaoDoLog = NewCard(_buildLog);
        cartaoDoLog.Controls.Add(_buildLogTitle);
        split.Panel2.Controls.Add(cartaoDoLog);

        // Depois de a janela existir: SplitterDistance lanca se for maior que a
        // altura atual do container, que no momento da montagem ainda e zero.
        split.HandleCreated += (_, _) =>
        {
            var desejado = (int)(split.Height * 0.55);
            if (desejado > split.Panel1MinSize && desejado < split.Height - split.Panel2MinSize)
                split.SplitterDistance = desejado;
        };

        return NewPage(header, split);
    }

    private Panel BuildServiceLogPage()
    {
        var header = new PageHeader(
            "Log do serviço",
            "Saída ao vivo do serviço: watchers, fila e publicação.");

        var limpar = new PillButton("Limpar", ButtonKind.Ghost) { Width = 90 };
        limpar.Click += (_, _) => { _liveLog.Clear(); _serviceLog.Limpar(); };
        header.Actions.Controls.Add(limpar);

        return NewPage(header, NewCard(_serviceLog));
    }

    private static Panel BuildTutorialPage()
    {
        var header = new PageHeader(
            "Tutorial",
            "Do zero até a primeira build: instalar, conectar ao GitHub e vincular um projeto.");

        return NewPage(header, new TutorialPage());
    }

    private Panel BuildConfigPage()
    {
        var header = new PageHeader(
            "Configuração",
            "Gravar passa pela mesma validação da inicialização do serviço.");

        return NewPage(header, _configPanel);
    }

    /// <summary>Envolve um controle num cartao, com folga para os cantos arredondados aparecerem.</summary>
    private static Card NewCard(Control content)
    {
        var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(10) };
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        return card;
    }

    // -------------------------------------------------------------------- dados

    /// <summary>1 enquanto uma atualizacao esta em curso.</summary>
    private int _refreshing;

    private void RefreshData()
    {
        var services = _controller.Services;
        if (services is null)
        {
            _projectsGrid.Rows.Clear();
            return;
        }

        // Uma de cada vez. O timer bate a cada tres segundos; se uma consulta
        // demorar mais que isso — banco num disco ocupado, muitas builds —, as
        // seguintes se empilhariam sobre a mesma conexao, e a fila so cresce.
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;

        // Fire-and-forget deliberado: o timer nao pode esperar I/O, e um
        // erro aqui so significa uma atualizacao perdida de tres segundos.
        _ = RefreshAsync(services);
    }

    private async Task RefreshAsync(IServiceProvider services)
    {
        try
        {
            var store = services.GetRequiredService<IBuildStore>();
            var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CiOptions>>().Value;
            var progresso = services.GetRequiredService<BuildProgress>();

            // Aqui entram tambem os desligados, que o servico ignora: sumir com
            // eles da tela faz a pessoa procurar um projeto que ela mesma
            // desligou e concluir que a configuracao se perdeu. Eles aparecem
            // apagados, dizendo DESLIGADO.
            var projects = options.Projects
                .Select(p => (Nome: p.Name, Ligado: p.Enabled))
                .ToList();

            var rows = new List<string[]>();
            var builds = new List<BuildRecord>();

            var running = await store.GetByStatusAsync(BuildStatus.Running, default);

            foreach (var project in projects)
            {
                var recent = await store.GetRecentAsync(project.Nome, 50, default);
                builds.AddRange(recent);

                var current = project.Ligado
                    ? running.FirstOrDefault(b =>
                        string.Equals(b.Project, project.Nome, StringComparison.OrdinalIgnoreCase))
                    : null;

                var last = recent.FirstOrDefault(b => b.FinishedAt is not null);

                rows.Add(!project.Ligado
                    ? [project.Nome, DisabledLabel, "nunca", "—", "—", "—"]
                    : current is not null
                        ?
                        [
                            project.Nome, RunningLabel, Local(current.StartedAt), current.ShortSha,
                            Andamento(progresso, current), "—",
                        ]
                        : last is null
                            ? [project.Nome, "—", "nunca", "—", "—", "—"]
                            :
                            [
                                project.Nome,
                                StatusFormatter.Label(last.Status),
                                Local(last.FinishedAt),
                                last.ShortSha,
                                StatusFormatter.FormatDuration(last.DurationSeconds),
                                last.PublishedPath ?? last.ArtifactPath ?? "—",
                            ]);
            }

            BeginInvoke(() =>
            {
                Fill(_projectsGrid, rows, statusColumn: 1, andamentoColumn: 4);
                Fill(_buildsGrid, builds
                    .OrderByDescending(b => b.Id)
                    .Take(200)
                    .Select(b => new[]
                    {
                        // A primeira e a ultima ficam vazias: quem desenha nelas
                        // e a roda e o botao da linha, nao um valor.
                        "",
                        b.Id.ToString(),
                        b.Project,
                        StatusFormatter.Label(b.Status),
                        Local(b.FinishedAt ?? b.QueuedAt),
                        StatusFormatter.FormatDuration(b.DurationSeconds),
                        b.ShortSha,
                        b.CommitAuthor ?? "—",
                        FirstLine(b.ErrorSummary),
                        "",
                    })
                    .ToList(), statusColumn: ColunaDoResultado);

                AtualizarRoda();

                // A grade seleciona a primeira linha assim que ela e criada,
                // antes de as celulas terem valor: naquele instante nao havia id
                // para carregar, e o painel de log ficava vazio ate alguem
                // clicar. Aqui ja ha.
                ShowSelectedBuildLog();

                UpdateStatus();
            });
        }
        catch (ObjectDisposedException) { /* janela fechando */ }
        catch (InvalidOperationException) { /* host reiniciando */ }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    /// <summary>
    /// Reescreve as celulas em vez de recriar as linhas: recriar faria a
    /// selecao e a posicao da rolagem saltarem a cada tres segundos.
    ///
    /// So a celula de resultado recebe cor. Pintar a linha inteira de vermelho
    /// deixava o resto — projeto, commit, autor — dificil de ler por um dado que
    /// cabe numa coluna so. A excecao e o projeto desligado: ali nao ha nenhum
    /// dado valendo, e a linha inteira apaga.
    /// </summary>
    /// <param name="andamentoColumn">
    /// Coluna que muda de cor enquanto a build corre. Fica em verde para separar
    /// o que esta acontecendo agora do que ja terminou.
    /// </param>
    private static void Fill(
        DataGridView grid, IReadOnlyList<string[]> rows, int statusColumn, int? andamentoColumn = null)
    {
        while (grid.Rows.Count > rows.Count) grid.Rows.RemoveAt(grid.Rows.Count - 1);
        while (grid.Rows.Count < rows.Count) grid.Rows.Add();

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Length && c < grid.ColumnCount; c++)
            {
                var value = rows[r][c];
                if (!Equals(grid.Rows[r].Cells[c].Value, value))
                    grid.Rows[r].Cells[c].Value = value;
            }

            if (statusColumn >= grid.ColumnCount) continue;

            var apagada = rows[r][statusColumn] == DisabledLabel;
            var cor = apagada ? Theme.TextFaint : Theme.Text;

            if (grid.Rows[r].DefaultCellStyle.ForeColor != cor)
            {
                grid.Rows[r].DefaultCellStyle.ForeColor = cor;
                grid.Rows[r].DefaultCellStyle.SelectionForeColor = cor;
            }

            if (andamentoColumn is not { } coluna || coluna >= grid.ColumnCount) continue;

            var tinta = rows[r][statusColumn] == RunningLabel ? Theme.AccentHover : cor;
            var celula = grid.Rows[r].Cells[coluna];

            if (celula.Style.ForeColor == tinta) continue;

            celula.Style.ForeColor = tinta;
            celula.Style.SelectionForeColor = tinta;
        }
    }

    /// <summary>O projeto cuja build esta correndo agora.</summary>
    private const string RunningLabel = "EM EXECUÇÃO";

    /// <summary>O projeto que existe na configuracao mas o servico nao observa.</summary>
    private const string DisabledLabel = "DESLIGADO";

    /// <summary>
    /// A coluna de estado e pintada a mao: bolinha na cor do estado, depois o
    /// rotulo em caixa alta na mesma cor.
    ///
    /// A bolinha nao e enfeite. Cor sozinha exclui quem nao distingue vermelho
    /// de verde, e e ela que deixa a coluna varrivel de relance numa grade de
    /// vinte linhas.
    /// </summary>
    private static void PaintStatusColumn(DataGridView grid, int statusColumn)
    {
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != statusColumn || e.Graphics is null) return;

            e.PaintBackground(e.CellBounds, true);

            var rotulo = e.FormattedValue as string ?? "";
            var cor = StatusColor(rotulo);

            var bolinha = new Rectangle(e.CellBounds.X + 8, e.CellBounds.Y, 7, e.CellBounds.Height);
            UiKit.StatusDot(e.Graphics, bolinha, cor);

            UiKit.Text(e.Graphics, rotulo, Theme.StatusLabel,
                new Rectangle(e.CellBounds.X + 21, e.CellBounds.Y, e.CellBounds.Width - 25, e.CellBounds.Height),
                cor, UiKit.LeftMiddle);

            e.Handled = true;
        };
    }

    /// <summary>
    /// A roda girando na primeira coluna, so na linha da build em execucao.
    ///
    /// E o unico elemento da tela que se mexe, de proposito: e ele que separa
    /// "esta acontecendo agora" de "aconteceu", sem precisar ler nada.
    /// </summary>
    private void PaintSpinnerColumn(DataGridView grid)
        => grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColunaDaRoda || e.Graphics is null) return;

            e.PaintBackground(e.CellBounds, true);

            if (EmExecucao(grid.Rows[e.RowIndex]))
                UiKit.Spinner(e.Graphics, e.CellBounds, Theme.AccentHover, _anguloDaRoda);

            e.Handled = true;
        };

    /// <summary>
    /// A ultima coluna: lixeira no que ja terminou, quadrado de parar no que
    /// esta correndo.
    ///
    /// Sao acoes diferentes no mesmo lugar porque sao a mesma pergunta — "quero
    /// que esta linha pare de existir" —, e porque uma build viva nao pode ser
    /// apagada: o pipeline ainda vai escrever nela.
    /// </summary>
    private void PaintActionColumn(DataGridView grid)
        => grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColunaDaAcao || e.Graphics is null) return;

            e.PaintBackground(e.CellBounds, true);

            var area = new Rectangle(
                e.CellBounds.X + (e.CellBounds.Width - 16) / 2,
                e.CellBounds.Y + (e.CellBounds.Height - 16) / 2,
                16, 16);

            if (EmExecucao(grid.Rows[e.RowIndex])) UiKit.StopGlyph(e.Graphics, area, Theme.Danger);
            else UiKit.TrashGlyph(e.Graphics, area, Theme.TextFaint);

            e.Handled = true;
        };

    private static bool EmExecucao(DataGridViewRow linha)
        => linha.Cells[ColunaDoResultado].Value as string == RunningLabel;

    /// <summary>
    /// Liga o timer da roda so quando ha build correndo. Uma janela aberta o dia
    /// inteiro nao pode repintar dez vezes por segundo para nao mostrar nada.
    /// </summary>
    private void AtualizarRoda()
    {
        var precisa = false;
        foreach (DataGridViewRow linha in _buildsGrid.Rows)
            if (EmExecucao(linha)) { precisa = true; break; }

        if (precisa == _spinner.Enabled) return;

        if (precisa) _spinner.Start();
        else { _spinner.Stop(); _buildsGrid.Invalidate(); }
    }

    private void InvalidateSpinnerCells()
    {
        for (var r = 0; r < _buildsGrid.Rows.Count; r++)
            if (EmExecucao(_buildsGrid.Rows[r]))
                _buildsGrid.InvalidateCell(ColunaDaRoda, r);
    }

    private void OnBuildsGridClick(DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != ColunaDaAcao) return;

        var linha = _buildsGrid.Rows[e.RowIndex];
        if (linha.Cells[ColunaDoId].Value is not string texto || !long.TryParse(texto, out var id)) return;

        if (EmExecucao(linha)) CancelBuild(id, linha.Cells[ColunaDoProjeto].Value as string);
        else DeleteBuild(id);
    }

    private static Color StatusColor(string? label) => label switch
    {
        "SUCESSO" => Theme.Success,
        "FALHOU" => Theme.Danger,
        "INTERROMPIDA" or "CANCELADA" => Theme.Warning,
        RunningLabel => Theme.AccentHover,
        "NA FILA" => Theme.Info,
        DisabledLabel => Theme.TextFaint,
        _ => Theme.TextMuted,
    };

    private void ShowSelectedBuildLog()
    {
        // Enquanto a pagina esta escondida a grade nao define linha corrente,
        // entao sem este recuo o painel de log ficava em branco ate alguem
        // clicar — inclusive na build que acabou de falhar.
        var linha = _buildsGrid.CurrentRow;
        if (linha is null && _buildsGrid.Rows.Count > 0)
        {
            linha = _buildsGrid.Rows[0];
            linha.Selected = true;
        }

        if (linha?.Cells[ColunaDoId].Value is not string idText || !long.TryParse(idText, out var id))
            return;

        if (_selectedBuildId == id) return;
        _selectedBuildId = id;

        // De qual build e o log que esta embaixo. Sem isto, trocar de linha
        // troca o conteudo do painel sem nada dizer que trocou.
        var projeto = linha.Cells[ColunaDoProjeto].Value as string;
        _buildLogTitle.Text = $"Log da build #{id}" + (projeto is null ? "" : " · " + projeto);

        var services = _controller.Services;
        if (services is null) return;

        _ = LoadBuildLogAsync(services, id);
    }

    private async Task LoadBuildLogAsync(IServiceProvider services, long id)
    {
        try
        {
            var record = await services.GetRequiredService<IBuildStore>().GetAsync(id, default);
            var text = record?.LogPath is { } path && File.Exists(path)
                ? await ReadSharedAsync(path)
                : "(log nao encontrado em disco)";

            BeginInvoke(() => _buildLog.Preencher(BuildLogLines(text)));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            BeginInvoke(() => _buildLog.Preencher(
                [LogEntry.Corrida("(nao foi possivel ler o log: " + exception.Message + ")", LogTone.Error)]));
        }
    }

    /// <summary>FileShare.ReadWrite: o log pode estar sendo escrito agora mesmo.</summary>
    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    // ------------------------------------------------------------------- acoes

    private void BuildSelectedProject()
    {
        var project = SelectedProjectName();
        if (project is null) return;

        var services = _controller.Services;
        if (services is null) { Warn("O servico esta parado."); return; }

        var resolved = ProjectResolver
            .ResolveEnabled(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CiOptions>>().Value)
            .FirstOrDefault(p => p.Name == project);

        if (resolved is null) return;

        _buildNow.Enabled = false;
        _ = Task.Run(async () =>
        {
            try
            {
                var trigger = services.GetRequiredService<BuildTriggerService>();
                await trigger.EnqueueHeadAsync(resolved, BuildTrigger.Manual, default);
            }
            catch (Exception exception)
            {
                BeginInvoke(() => Warn("Nao foi possivel enfileirar: " + exception.Message));
            }
            finally
            {
                BeginInvoke(() => _buildNow.Enabled = true);
            }
        });
    }

    private void OpenSelectedFolder()
    {
        var project = SelectedProjectName();
        var services = _controller.Services;
        if (project is null || services is null) return;

        var resolved = ProjectResolver
            .ResolveEnabled(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CiOptions>>().Value)
            .FirstOrDefault(p => p.Name == project);

        if (resolved is null || string.IsNullOrWhiteSpace(resolved.Publishing.ArtifactFolder)) return;

        // Com prazo: a pasta de destino costuma ser um compartilhamento de rede,
        // e perguntar por uma maquina desligada prende a janela ate o Windows
        // desistir.
        if (!BoundedIo.Run(() => Directory.Exists(resolved.Publishing.ArtifactFolder)))
        {
            Warn("A pasta de destino ainda nao existe:\n" + resolved.Publishing.ArtifactFolder);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{resolved.Publishing.ArtifactFolder}\""));
    }

    private void RepublishPending()
    {
        var services = _controller.Services;
        if (services is null) { Warn("O servico esta parado."); return; }

        _republish.Enabled = false;
        _ = Task.Run(async () =>
        {
            var sent = 0;
            string? failure = null;

            try
            {
                sent = await services.GetRequiredService<IPendingCopyService>().RetryAllAsync(default);
            }
            catch (Exception exception) { failure = exception.Message; }

            BeginInvoke(() =>
            {
                _republish.Enabled = true;
                if (failure is not null) Warn("Falha ao reenviar: " + failure);
                else Inform(sent == 0 ? "Nao ha copias pendentes." : $"{sent} artefato(s) reenviado(s).");
            });
        });
    }

    private void ToggleHost()
    {
        _toggleHost.Enabled = false;
        var parar = _controller.State == HostState.Rodando;

        _ = Task.Run(async () =>
        {
            if (parar) await _controller.StopAsync();
            else await _controller.StartAsync();

            BeginInvoke(() => _toggleHost.Enabled = true);
        });
    }

    private string? SelectedProjectName()
        => _projectsGrid.CurrentRow?.Cells[0].Value as string;

    // ------------------------------------------------------------------ estado

    private void OnStateChanged()
    {
        if (IsHandleCreated) BeginInvoke(UpdateStatus);
    }

    private void UpdateStatus()
    {
        _toggleHost.Text = _controller.State == HostState.Rodando ? "Parar serviço" : "Iniciar serviço";

        var (texto, cor) = _controller.State switch
        {
            HostState.Rodando => ("Serviço em execução", Theme.Success),
            HostState.Iniciando => ("Iniciando...", Theme.Warning),
            HostState.Parado => ("Serviço parado", Theme.TextFaint),
            _ => ("Falhou ao iniciar: " + string.Join("  |  ", _controller.StartupErrors), Theme.Danger),
        };

        var direita = "";
        var rodando = _controller.State == HostState.Rodando;
        var pausada = false;

        if (rodando && _controller.Services is not null)
        {
            var snapshot = _controller.Services.GetRequiredService<IBuildScheduler>().Snapshot();
            pausada = snapshot.Paused;

            direita = $"fila {snapshot.Waiting}   ·   em execução {snapshot.Running} de {snapshot.MaxConcurrentBuilds}";
            if (pausada) direita += "   ·   fila pausada";
        }

        // A fila pausada nao muda o estado do servico: ele continua observando
        // os repositorios e enfileirando. Por isso ela aparece na direita, junto
        // dos numeros da fila, e nao no lugar de "Servico em execucao".
        if (pausada) cor = Theme.Warning;

        _status.Set(texto, cor, direita);

        _pausarFila.Text = pausada ? "Retomar fila" : "Pausar fila";

        _buildNow.Enabled = rodando;
        _republish.Enabled = rodando;
        _pausarFila.Enabled = rodando;
        _cancelarBuild.Enabled = rodando;
        _limparHistorico.Enabled = rodando;
    }

    private void LoadServiceLog()
        => _serviceLog.Preencher(_liveLog.Snapshot().Select(Format));

    private void OnLogLine(LogLine line)
    {
        if (!IsHandleCreated) return;

        try
        {
            BeginInvoke(() => _serviceLog.Anexar(Format(line)));
        }
        catch (ObjectDisposedException) { /* janela fechando */ }
        catch (InvalidOperationException) { /* handle indo embora */ }
    }

    /// <summary>
    /// Uma linha do servico virando colunas: hora, nivel em tres letras,
    /// categoria e mensagem.
    ///
    /// O nivel e abreviado de proposito. 'Information' e 'Warning' tem larguras
    /// diferentes e empurrariam a categoria de linha para linha; com INF, WRN e
    /// ERR as quatro colunas ficam paradas no lugar, e e isso que permite achar
    /// os erros descendo o olho pela coluna em vez de ler tudo.
    /// </summary>
    private static LogEntry Format(LogLine line) => new(
        line.At.ToString("HH:mm:ss"),
        Nivel(line.Level),
        line.Category,
        line.Message,
        line.Level switch
        {
            LogLevel.Error or LogLevel.Critical => LogTone.Error,
            LogLevel.Warning => LogTone.Warning,
            LogLevel.Trace or LogLevel.Debug => LogTone.Muted,
            _ => LogTone.Normal,
        });

    private static string Nivel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "",
    };

    /// <summary>
    /// O log de build em colunas: hora, etapa e mensagem.
    ///
    /// A hora vem carimbada no arquivo, linha a linha. A etapa nao: ela e
    /// deduzida dos marcadores '--- Sync ---' que o pipeline escreve, e vale
    /// dali para baixo. E assim que as dezessete mil linhas que o Unity despeja
    /// ganham a coluna de etapa sem o pipeline ter de repeti-la em cada uma.
    ///
    /// A mensagem em si nao e reformatada: quem procura um erro do Unity
    /// procura pelo texto exato que o Unity escreveu.
    /// </summary>
    private static IEnumerable<LogEntry> BuildLogLines(string texto)
    {
        var etapa = "";

        foreach (var bruta in texto.Split('\n'))
        {
            var (hora, linha) = BuildLogStamp.Split(bruta.TrimEnd('\r'));

            // O marcador de etapa deixa de ser uma linha de conteudo: agora ele
            // e a coluna. Vira uma linha so, dizendo que a etapa comecou.
            if (linha.StartsWith("--- ", StringComparison.Ordinal) && linha.EndsWith(" ---", StringComparison.Ordinal))
            {
                etapa = linha[4..^4].Trim();
                yield return new LogEntry(hora, "", etapa, "etapa iniciada", LogTone.Muted);
                continue;
            }

            var tom =
                linha.StartsWith("ETAPA ", StringComparison.Ordinal) ||
                linha.Contains(UnityLogParser.MarkedErrorPrefix, StringComparison.Ordinal) ||
                linha.Contains("): error ", StringComparison.Ordinal) ? LogTone.Error :

                linha.StartsWith("AVISO:", StringComparison.Ordinal) ||
                linha.Contains(UnityLogParser.MarkedWarningPrefix, StringComparison.Ordinal) ||
                linha.Contains("): warning ", StringComparison.Ordinal) ? LogTone.Warning :

                linha.StartsWith("===", StringComparison.Ordinal) ? LogTone.Muted :

                LogTone.Normal;

            // As linhas de abertura e de resultado sao do arquivo inteiro, e nao
            // de uma etapa: a coluna fica vazia nelas de proposito.
            var daLinha = linha.StartsWith("===", StringComparison.Ordinal) ? "" : etapa;

            yield return new LogEntry(hora, "", daLinha, linha, tom);
        }
    }

    // --------------------------------------------------------------- utilidades

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        BackgroundColor = Theme.Surface,
        BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        ScrollBars = ScrollBars.Both,
    };

    /// <summary>A ultima coluna ocupa a sobra, para nao ficar um vazio a direita.</summary>
    private static void StretchLastColumn(DataGridView grid)
    {
        var last = grid.Columns[grid.ColumnCount - 1];
        last.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        last.MinimumWidth = 160;
    }

    private static DataGridViewTextBoxColumn TextColumn(string header, int width)
        => new() { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };

    /// <summary>
    /// Coluna de dado tecnico — sha, caminho. Monoespacada porque se compara
    /// caractere a caractere com o que esta no Git, e nao se le como frase.
    /// </summary>
    private static DataGridViewTextBoxColumn MonoColumn(string header, int width)
    {
        var coluna = TextColumn(header, width);
        coluna.DefaultCellStyle.Font = Theme.Mono;
        return coluna;
    }

    private static string Local(DateTimeOffset? value)
        => value is null ? "—" : value.Value.ToLocalTime().ToString("dd/MM HH:mm");

    /// <summary>
    /// A coluna de duracao enquanto a build corre: a etapa e ha quanto tempo
    /// ela comecou.
    ///
    /// "EM EXECUCAO" sozinho e igual aos dois minutos e aos vinte, e a duvida
    /// de quem olha — esta andando ou travou? — nao tem resposta na tela. O
    /// tempo e contado do StartedAt na hora, sem guardar nada.
    /// </summary>
    private static string Andamento(BuildProgress progresso, BuildRecord build)
    {
        if (build.StartedAt is not { } inicio) return "—";

        // mm:ss, e nao "4 min 12 s": esta celula e relida a cada tres segundos
        // e o relogio precisa ocupar sempre a mesma largura, senao o numero
        // dança na coluna.
        var corrido = DateTimeOffset.UtcNow - inicio;
        var relogio = $"{(int)corrido.TotalMinutes:00}:{corrido.Seconds:00}";

        var etapa = progresso.Etapa(build.Id);
        return etapa is null ? relogio : etapa + " · " + relogio;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var index = text.IndexOf('\n');
        return (index >= 0 ? text[..index] : text).Trim();
    }

    // ------------------------------------------------------- acoes das builds

    /// <summary>
    /// Encerra a build que esta correndo.
    ///
    /// Pede confirmacao porque uma build de WebGL leva quinze minutos e nao ha
    /// como desfazer — e porque o botao fica a um clique de distancia da
    /// lixeira, que faz outra coisa.
    /// </summary>
    private void CancelBuild(long id, string? projeto)
    {
        var alvo = projeto is null ? $"a build #{id}" : $"a build #{id} de {projeto}";

        var resposta = MessageBox.Show(
            this,
            $"Cancelar {alvo}?" + Environment.NewLine + Environment.NewLine +
            "O Unity é encerrado junto, com os processos filhos. Nada é publicado, e a build fica " +
            "no histórico como Cancelada.",
            AppNames.Display, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (resposta != DialogResult.Yes) return;

        if (_controller.Services?.GetService<IBuildScheduler>() is not { } scheduler)
        {
            Warn("O serviço não está em execução.");
            return;
        }

        if (!scheduler.Cancel(id))
        {
            // Terminou entre o clique e o cancelamento. Nao e erro, mas quem
            // clicou precisa saber por que nada aconteceu.
            Inform($"A build #{id} já havia terminado.");
        }

        RefreshData();
    }

    private void CancelRunningBuild()
    {
        foreach (DataGridViewRow linha in _buildsGrid.Rows)
        {
            if (!EmExecucao(linha)) continue;
            if (linha.Cells[ColunaDoId].Value is not string texto || !long.TryParse(texto, out var id)) continue;

            CancelBuild(id, linha.Cells[ColunaDoProjeto].Value as string);
            return;
        }

        Inform("Nenhuma build em execução.");
    }

    private void DeleteBuild(long id)
    {
        var store = _controller.Services?.GetService<IBuildStore>();
        if (store is null)
        {
            Warn("O serviço precisa estar em execução para mexer no histórico.");
            return;
        }

        _ = ApagarAsync();

        async Task ApagarAsync()
        {
            try
            {
                var saiu = await store.DeleteAsync(id, default);
                BeginInvoke(() =>
                {
                    if (!saiu) Inform($"A build #{id} está em execução ou na fila: ela sai do histórico quando terminar.");
                    RefreshData();
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                BeginInvoke(() => Warn("Não foi possível apagar: " + exception.Message));
            }
        }
    }

    /// <summary>
    /// Limpa o historico inteiro, menos o que ainda esta vivo.
    ///
    /// So o registro sai. O log de cada build continua em disco e o zip
    /// publicado continua na pasta de destino: quem limpa a tela quer a tela
    /// limpa, nao quer perder o artefato que o time esta usando.
    /// </summary>
    private void ClearHistory()
    {
        var store = _controller.Services?.GetService<IBuildStore>();
        if (store is null)
        {
            Warn("O serviço precisa estar em execução para mexer no histórico.");
            return;
        }

        var resposta = MessageBox.Show(
            this,
            "Apagar do histórico todas as builds que já terminaram?" + Environment.NewLine + Environment.NewLine +
            "O que está em execução ou na fila fica. Os logs em disco e os artefatos já publicados " +
            "não são tocados.",
            AppNames.Display, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (resposta != DialogResult.Yes) return;

        _ = LimparAsync();

        async Task LimparAsync()
        {
            try
            {
                var quantas = await store.DeleteFinishedAsync(null, default);
                BeginInvoke(() =>
                {
                    _selectedBuildId = null;
                    _buildLog.Limpar();
                    _buildLogTitle.Text = "Log da build";
                    RefreshData();
                    Inform($"{quantas} build(s) saíram do histórico.");
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                BeginInvoke(() => Warn("Não foi possível limpar: " + exception.Message));
            }
        }
    }

    /// <summary>
    /// Segura a fila, sem tocar na build que ja esta correndo.
    ///
    /// Congelar o Unity no meio de uma importacao seria o que a palavra "pausar"
    /// sugere, e e justamente o que nao se pode fazer: ele fica com o lock da
    /// Library na mao e o cache azeda. O que da para segurar — e o que costuma
    /// ser o pedido de verdade — e a proxima.
    /// </summary>
    private void TogglePause()
    {
        if (_controller.Services?.GetService<IBuildScheduler>() is not { } scheduler)
        {
            Warn("O serviço não está em execução.");
            return;
        }

        scheduler.Paused = !scheduler.Paused;
        UpdateStatus();
    }

    private void Warn(string message)
        => MessageBox.Show(this, message, AppNames.Display, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void Inform(string message)
        => MessageBox.Show(this, message, AppNames.Display, MessageBoxButtons.OK, MessageBoxIcon.Information);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // A regra e por exclusao, e nao por CloseReason.UserClosing: fechar
        // esconde SEMPRE, menos quando saimos de proposito pela bandeja ou
        // quando o Windows esta encerrando. Testar so o UserClosing deixava o
        // app morrer em qualquer fechamento que chegasse por outro caminho, e
        // um CI que para de observar sem ninguem pedir e o pior defeito
        // possivel aqui.
        var encerrandoDeVerdade = ExitRequested
            || e.CloseReason is CloseReason.WindowsShutDown
                            or CloseReason.TaskManagerClosing
                            or CloseReason.ApplicationExitCall;

        if (!encerrandoDeVerdade)
        {
            e.Cancel = true;
            Tray?.HideToTray();
            return;
        }

        _refresh.Stop();
        _liveLog.LineAdded -= OnLogLine;
        _controller.StateChanged -= OnStateChanged;

        base.OnFormClosing(e);
    }
}
