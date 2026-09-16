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
/// A janela do UnityLocalCI. Quatro abas: projetos, builds, log e configuracao.
///
/// Ela nao guarda estado proprio: tudo que mostra vem do mesmo SQLite que o
/// servico escreve. Assim a janela nunca discorda do que aconteceu de verdade,
/// e fecha-la nao perde nada.
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
    private readonly TextBox _buildLog = NewMonospaceBox();
    private readonly TextBox _serviceLog = NewMonospaceBox();
    private readonly ConfigPanel _configPanel;

    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 0, 0),
    };

    private readonly Button _buildNow = new() { Text = "Construir agora", Width = 130, Height = 28 };
    private readonly Button _openFolder = new() { Text = "Abrir pasta", Width = 100, Height = 28 };
    private readonly Button _republish = new() { Text = "Reenviar pendentes", Width = 140, Height = 28 };
    private readonly Button _toggleHost = new() { Text = "Parar servico", Width = 110, Height = 28 };

    private long? _selectedBuildId;

    public MainForm(HostController controller, LiveLog liveLog, string configPath)
    {
        _controller = controller;
        _liveLog = liveLog;
        _configPath = configPath;
        _configPanel = new ConfigPanel(configPath, controller);

        Text = "UnityLocalCI";
        Width = 1100;
        Height = 700;
        MinimumSize = new Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();

        _controller.StateChanged += OnStateChanged;
        _liveLog.LineAdded += OnLogLine;

        _refresh.Interval = (int)RefreshInterval.TotalMilliseconds;
        _refresh.Tick += (_, _) => RefreshData();
        _refresh.Start();

        LoadServiceLog();
        RefreshData();
        UpdateStatus();
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildProjectsTab());
        tabs.TabPages.Add(BuildBuildsTab());
        tabs.TabPages.Add(BuildServiceLogTab());
        tabs.TabPages.Add(BuildConfigTab());

        var statusStrip = new Panel { Dock = DockStyle.Bottom, Height = 30 };
        statusStrip.Controls.Add(_status);

        Controls.Add(tabs);
        Controls.Add(statusStrip);
    }

    private TabPage BuildProjectsTab()
    {
        var page = new TabPage("Projetos") { Padding = new Padding(8) };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(0, 6, 0, 0),
        };

        _buildNow.Click += (_, _) => BuildSelectedProject();
        _openFolder.Click += (_, _) => OpenSelectedFolder();
        _republish.Click += (_, _) => RepublishPending();
        _toggleHost.Click += (_, _) => ToggleHost();

        buttons.Controls.AddRange(new Control[] { _buildNow, _openFolder, _republish, _toggleHost });

        _projectsGrid.Dock = DockStyle.Fill;
        _projectsGrid.Columns.AddRange(
            TextColumn("Projeto", 130),
            TextColumn("Estado", 100),
            TextColumn("Última build", 130),
            TextColumn("Commit", 80),
            TextColumn("Duração", 90),
            TextColumn("Artefato", 300));

        page.Controls.Add(_projectsGrid);
        page.Controls.Add(buttons);
        return page;
    }

    private TabPage BuildBuildsTab()
    {
        var page = new TabPage("Builds") { Padding = new Padding(8) };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 250,
        };

        _buildsGrid.Dock = DockStyle.Fill;
        _buildsGrid.Columns.AddRange(
            TextColumn("#", 55),
            TextColumn("Projeto", 110),
            TextColumn("Resultado", 100),
            TextColumn("Quando", 130),
            TextColumn("Duração", 90),
            TextColumn("Commit", 80),
            TextColumn("Autor", 150),
            TextColumn("Erro", 320));

        _buildsGrid.SelectionChanged += (_, _) => ShowSelectedBuildLog();

        split.Panel1.Controls.Add(_buildsGrid);
        split.Panel2.Controls.Add(_buildLog);

        page.Controls.Add(split);
        return page;
    }

    private TabPage BuildServiceLogTab()
    {
        var page = new TabPage("Log do serviço") { Padding = new Padding(8) };

        var clear = new Button { Text = "Limpar", Dock = DockStyle.Bottom, Height = 28 };
        clear.Click += (_, _) => { _liveLog.Clear(); _serviceLog.Clear(); };

        page.Controls.Add(_serviceLog);
        page.Controls.Add(clear);
        return page;
    }

    private TabPage BuildConfigTab()
    {
        var page = new TabPage("Configuração") { Padding = new Padding(8) };
        _configPanel.Dock = DockStyle.Fill;
        page.Controls.Add(_configPanel);
        return page;
    }

    // -------------------------------------------------------------------- dados

    private void RefreshData()
    {
        var services = _controller.Services;
        if (services is null)
        {
            _projectsGrid.Rows.Clear();
            return;
        }

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
                    ? new[] { project.Name, "construindo", Local(current.StartedAt), current.ShortSha, "—", "—" }
                    : last is null
                        ? new[] { project.Name, "—", "nunca", "—", "—", "—" }
                        : new[]
                        {
                            project.Name,
                            StatusFormatter.Label(last.Status),
                            Local(last.FinishedAt),
                            last.ShortSha,
                            StatusFormatter.FormatDuration(last.DurationSeconds),
                            last.PublishedPath ?? last.ArtifactPath ?? "—",
                        });
            }

            BeginInvoke(() =>
            {
                Fill(_projectsGrid, rows);
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
                    .ToList());

                UpdateStatus();
            });
        }
        catch (ObjectDisposedException) { /* janela fechando */ }
        catch (InvalidOperationException) { /* host reiniciando */ }
    }

    /// <summary>
    /// Reescreve as celulas em vez de recriar as linhas: recriar faria a
    /// selecao e a posicao da rolagem saltarem a cada tres segundos.
    /// </summary>
    private static void Fill(DataGridView grid, IReadOnlyList<string[]> rows)
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

            grid.Rows[r].DefaultCellStyle.ForeColor = rows[r].Any(v => v is "FALHOU" or "INTERROMPIDA")
                ? Color.Firebrick
                : grid.DefaultCellStyle.ForeColor;
        }
    }

    private void ShowSelectedBuildLog()
    {
        if (_buildsGrid.CurrentRow?.Cells[0].Value is not string idText || !long.TryParse(idText, out var id))
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

        if (!Directory.Exists(resolved.Publishing.ArtifactFolder))
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

        var texto = _controller.State switch
        {
            HostState.Rodando => "Serviço em execução",
            HostState.Iniciando => "Iniciando...",
            HostState.Parado => "Serviço parado",
            _ => "Falhou ao iniciar: " + string.Join("  |  ", _controller.StartupErrors),
        };

        if (_controller.State == HostState.Rodando && _controller.Services is not null)
        {
            var snapshot = _controller.Services.GetRequiredService<IBuildScheduler>().Snapshot();
            texto += $"   ·   fila: {snapshot.Waiting}   ·   em execução: {snapshot.Running} de {snapshot.MaxConcurrentBuilds}";
        }

        _status.Text = texto;
        _status.ForeColor = _controller.State == HostState.Falhou ? Color.Firebrick : SystemColors.ControlText;

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
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
    };

    private static TextBox NewMonospaceBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9f),
        BackColor = SystemColors.Window,
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
        // Fechar pelo X esconde na bandeja: o servico continua construindo, que
        // e o comportamento que se espera de um CI.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _refresh.Stop();
        _liveLog.LineAdded -= OnLogLine;
        _controller.StateChanged -= OnStateChanged;

        base.OnFormClosing(e);
    }
}
