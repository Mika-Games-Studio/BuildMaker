using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Hosting;
using UnityLocalCI.Core.Publishing;
using UnityLocalCI.Core.Queue;
using UnityLocalCI.Core.State;
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

    /// <summary>Teto do texto da aba de log, em caracteres.</summary>
    private const int MaxServiceLogChars = 400_000;

    private readonly HostController _controller;
    private readonly LiveLog _liveLog;
    private readonly string _configPath;
    private readonly System.Windows.Forms.Timer _refresh = new();

    private readonly DataGridView _projectsGrid = NewGrid();
    private readonly DataGridView _buildsGrid = NewGrid();
    private readonly TextBox _buildLog = NewMonospaceBox();
    private readonly TextBox _serviceLog = NewMonospaceBox();
    private readonly ConfigPanel _configPanel;

    private readonly StatusBar _status = new();
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
    private readonly List<Panel> _pages = [];

    private readonly PillButton _buildNow = new("Construir agora", ButtonKind.Primary) { Width = 136 };
    private readonly PillButton _openFolder = new("Abrir pasta") { Width = 104 };
    private readonly PillButton _republish = new("Reenviar pendentes", ButtonKind.Ghost) { Width = 148 };
    private readonly PillButton _toggleHost = new("Parar serviço", ButtonKind.Ghost) { Width = 116 };

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

        Text = "UnityLocalCI";
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

        var rail = new NavRail { Dock = DockStyle.Left, HeaderMark = AppIcon.LoadMark() };
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
            TextColumn("Commit", 90),
            TextColumn("Duração", 90),
            TextColumn("Artefato", 300));

        StretchLastColumn(_projectsGrid);

        return NewPage(header, NewCard(_projectsGrid));
    }

    private Panel BuildBuildsPage()
    {
        var header = new PageHeader(
            "Builds",
            "Histórico das execuções. Selecione uma linha para ler o log dela.");

        _buildsGrid.Columns.AddRange(
            TextColumn("#", 60),
            TextColumn("Projeto", 120),
            TextColumn("Resultado", 110),
            TextColumn("Quando", 110),
            TextColumn("Duração", 90),
            TextColumn("Commit", 90),
            TextColumn("Autor", 130),
            TextColumn("Erro", 320));

        StretchLastColumn(_buildsGrid);
        _buildsGrid.SelectionChanged += (_, _) => ShowSelectedBuildLog();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 10,
            BackColor = Theme.Canvas,
        };

        split.Panel1.Controls.Add(NewCard(_buildsGrid));
        split.Panel2.Controls.Add(NewCard(_buildLog));

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
        limpar.Click += (_, _) => { _liveLog.Clear(); _serviceLog.Clear(); };
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
            var projects = ProjectResolver.ResolveEnabled(options);

            var rows = new List<string[]>();
            var builds = new List<BuildRecord>();

            var running = await store.GetByStatusAsync(BuildStatus.Running, default);

            foreach (var project in projects)
            {
                var recent = await store.GetRecentAsync(project.Name, 50, default);
                builds.AddRange(recent);

                var current = running.FirstOrDefault(b =>
                    string.Equals(b.Project, project.Name, StringComparison.OrdinalIgnoreCase));

                var last = recent.FirstOrDefault(b => b.FinishedAt is not null);

                rows.Add(current is not null
                    ? [project.Name, "EM EXECUÇÃO", Local(current.StartedAt), current.ShortSha, "—", "—"]
                    : last is null
                        ? [project.Name, "—", "nunca", "—", "—", "—"]
                        :
                        [
                            project.Name,
                            StatusFormatter.Label(last.Status),
                            Local(last.FinishedAt),
                            last.ShortSha,
                            StatusFormatter.FormatDuration(last.DurationSeconds),
                            last.PublishedPath ?? last.ArtifactPath ?? "—",
                        ]);
            }

            BeginInvoke(() =>
            {
                Fill(_projectsGrid, rows, statusColumn: 1);
                Fill(_buildsGrid, builds
                    .OrderByDescending(b => b.Id)
                    .Take(200)
                    .Select(b => new[]
                    {
                        b.Id.ToString(),
                        b.Project,
                        StatusFormatter.Label(b.Status),
                        Local(b.FinishedAt ?? b.QueuedAt),
                        StatusFormatter.FormatDuration(b.DurationSeconds),
                        b.ShortSha,
                        b.CommitAuthor ?? "—",
                        FirstLine(b.ErrorSummary),
                    })
                    .ToList(), statusColumn: 2);

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
    /// cabe numa coluna so.
    /// </summary>
    private static void Fill(DataGridView grid, IReadOnlyList<string[]> rows, int statusColumn)
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

            var celula = grid.Rows[r].Cells[statusColumn];
            celula.Style.ForeColor = StatusColor(celula.Value as string);
            celula.Style.SelectionForeColor = celula.Style.ForeColor;
            celula.Style.Font = Theme.UiSmallBold;
        }
    }

    private static Color StatusColor(string? label) => label switch
    {
        "SUCESSO" => Theme.Success,
        "FALHOU" => Theme.Danger,
        "INTERROMPIDA" or "CANCELADA" => Theme.Warning,
        "EM EXECUÇÃO" => Theme.Accent,
        "NA FILA" => Theme.Info,
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

        if (linha?.Cells[0].Value is not string idText || !long.TryParse(idText, out var id))
            return;

        if (_selectedBuildId == id) return;
        _selectedBuildId = id;

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

            BeginInvoke(() => _buildLog.Text = text);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            BeginInvoke(() => _buildLog.Text = "(nao foi possivel ler o log: " + exception.Message + ")");
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
        if (_controller.State == HostState.Rodando && _controller.Services is not null)
        {
            var snapshot = _controller.Services.GetRequiredService<IBuildScheduler>().Snapshot();
            direita = $"fila {snapshot.Waiting}   ·   em execução {snapshot.Running} de {snapshot.MaxConcurrentBuilds}";
        }

        _status.Set(texto, cor, direita);

        _buildNow.Enabled = _controller.State == HostState.Rodando;
        _republish.Enabled = _controller.State == HostState.Rodando;
    }

    private void LoadServiceLog()
    {
        _serviceLog.Lines = _liveLog.Snapshot().Select(Format).ToArray();
        ScrollToEnd(_serviceLog);
    }

    private void OnLogLine(LogLine line)
    {
        if (!IsHandleCreated) return;

        try
        {
            BeginInvoke(() =>
            {
                // A caixa cresce para sempre; o LiveLog, nao. Passando do teto,
                // ela e recarregada das ultimas linhas que ele guarda. Sem isto,
                // um programa que fica semanas aberto vai ficando lento a cada
                // repintura de um texto de megabytes.
                if (_serviceLog.TextLength > MaxServiceLogChars) LoadServiceLog();

                _serviceLog.AppendText(Format(line) + Environment.NewLine);
                ScrollToEnd(_serviceLog);
            });
        }
        catch (ObjectDisposedException) { /* janela fechando */ }
        catch (InvalidOperationException) { /* handle indo embora */ }
    }

    private static string Format(LogLine line)
        => $"{line.At:HH:mm:ss}  {line.Level.ToString().ToLowerInvariant()[..4],-4}  {line.Category,-22}  {line.Message}";

    private static void ScrollToEnd(TextBox box)
    {
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
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

    private static TextBox NewMonospaceBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = Theme.Mono,
        BackColor = Theme.Surface,
        ForeColor = Theme.Blend(Theme.Text, Theme.TextMuted, 0.35),
        BorderStyle = BorderStyle.None,
    };

    private static DataGridViewTextBoxColumn TextColumn(string header, int width)
        => new() { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };

    private static string Local(DateTimeOffset? value)
        => value is null ? "—" : value.Value.ToLocalTime().ToString("dd/MM HH:mm");

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var index = text.IndexOf('\n');
        return (index >= 0 ? text[..index] : text).Trim();
    }

    private void Warn(string message)
        => MessageBox.Show(this, message, "UnityLocalCI", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void Inform(string message)
        => MessageBox.Show(this, message, "UnityLocalCI", MessageBoxButtons.OK, MessageBoxIcon.Information);

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
