using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Hosting;
using UnityLocalCI.Core.Secrets;

namespace UnityLocalCI.App;

/// <summary>
/// Edicao do appsettings.json pela janela.
///
/// Gravar passa pela mesma validacao da inicializacao do servico, entao nao da
/// para salvar algo que derrubaria o proximo start. E como os watchers sao
/// montados a partir da configuracao, salvar oferece reiniciar o servico: e
/// mais honesto que fingir que a troca vale a quente.
/// </summary>
public sealed class ConfigPanel : UserControl
{
    private readonly string _configPath;
    private readonly HostController _controller;
    private readonly ICredentialStore _credentials = new WindowsCredentialStore();

    private readonly PropertyGrid _schedulerGrid = NewPropertyGrid();
    private readonly PropertyGrid _projectGrid = NewPropertyGrid();

    private readonly DarkListBox _projectList = new() { Dock = DockStyle.Fill };

    private readonly Label _problems = new()
    {
        Dock = DockStyle.Bottom,
        AutoSize = false,
        Height = 64,
        ForeColor = Theme.Danger,
        Padding = new Padding(2, 8, 2, 4),
    };

    private CiOptions _options = new();

    public ConfigPanel(string configPath, HostController controller)
    {
        _configPath = configPath;
        _controller = controller;

        BuildLayout();
        Reload();
    }

    private void BuildLayout()
    {
        BackColor = Theme.Canvas;

        var tabs = new SegmentedTabControl { Dock = DockStyle.Fill };

        // --- geral
        var geral = new TabPage("Geral") { Padding = new Padding(0, 10, 0, 0) };
        _schedulerGrid.Dock = DockStyle.Fill;
        geral.Controls.Add(WrapInCard(_schedulerGrid));
        tabs.TabPages.Add(geral);

        // --- projetos
        var projetos = new TabPage("Projetos") { Padding = new Padding(0, 10, 0, 0) };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterWidth = 10,
            BackColor = Theme.Canvas,
        };

        var listaBotoes = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = Theme.Surface,
        };

        var adicionar = new PillButton("Adicionar") { Width = 96, Backdrop = Theme.Surface };
        var remover = new PillButton("Remover", ButtonKind.Ghost) { Width = 92, Backdrop = Theme.Surface };
        adicionar.Click += (_, _) => AddProject();
        remover.Click += (_, _) => RemoveProject();
        listaBotoes.Controls.AddRange([adicionar, remover]);

        _projectList.SelectedIndexChanged += (_, _) => ShowSelectedProject();

        var listaCartao = WrapInCard(_projectList);
        listaCartao.Controls.Add(listaBotoes);

        split.Panel1.Controls.Add(listaCartao);
        split.Panel2.Controls.Add(WrapInCard(_projectGrid));

        // Depois de a janela existir: SplitterDistance lanca enquanto a largura
        // do container ainda e zero.
        split.HandleCreated += (_, _) =>
        {
            if (split.Width > 460) split.SplitterDistance = 240;
        };

        projetos.Controls.Add(split);
        tabs.TabPages.Add(projetos);

        // --- rodape
        var acoes = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(0, 10, 0, 0),
            BackColor = Theme.Canvas,
        };

        var salvarReiniciar = new PillButton("Salvar e reiniciar", ButtonKind.Primary) { Width = 152 };
        var salvar = new PillButton("Salvar") { Width = 96 };
        var descartar = new PillButton("Descartar", ButtonKind.Ghost) { Width = 100 };
        var abrirArquivo = new PillButton("Abrir o arquivo", ButtonKind.Ghost) { Width = 128 };

        salvar.Click += (_, _) => Save(restart: false);
        salvarReiniciar.Click += (_, _) => Save(restart: true);
        descartar.Click += (_, _) => Reload();
        abrirArquivo.Click += (_, _) => OpenInEditor();

        acoes.Controls.AddRange([salvarReiniciar, salvar, descartar, abrirArquivo]);

        Controls.Add(tabs);
        Controls.Add(_problems);
        Controls.Add(acoes);
    }

    private static Card WrapInCard(Control content)
    {
        var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(10) };
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        return card;
    }

    // ------------------------------------------------------------------ dados

    private void Reload()
    {
        try
        {
            _options = ConfigFile.Load(_configPath);
            Report("", problema: false);
        }
        catch (Exception exception)
        {
            _options = new CiOptions();
            Report("Não foi possível ler o arquivo: " + exception.Message, problema: true);
        }

        _schedulerGrid.SelectedObject = _options.Scheduler;
        RefreshProjectList();
    }

    private void RefreshProjectList()
    {
        var selected = _projectList.SelectedIndex;

        _projectList.BeginUpdate();
        _projectList.Items.Clear();
        foreach (var project in _options.Projects)
        {
            var nome = string.IsNullOrWhiteSpace(project.Name) ? "(sem nome)" : project.Name;
            _projectList.Items.Add(project.Enabled ? nome : nome + "  (desligado)");
        }
        _projectList.EndUpdate();

        if (_projectList.Items.Count > 0)
            _projectList.SelectedIndex = Math.Clamp(selected, 0, _projectList.Items.Count - 1);
        else
            _projectGrid.SelectedObject = null;
    }

    private void ShowSelectedProject()
    {
        var index = _projectList.SelectedIndex;
        _projectGrid.SelectedObject = index >= 0 && index < _options.Projects.Count
            ? new ProjectView(_options.Projects[index])
            : null;
    }

    private void AddProject()
    {
        _options.Projects.Add(new ProjectOptions
        {
            Name = "NovoProjeto",
            Enabled = false,
            Repository = new RepositoryOptions { Branch = "HML" },
            Publishing = new PublishingOptions(),
        });

        RefreshProjectList();
        _projectList.SelectedIndex = _options.Projects.Count - 1;
    }

    private void RemoveProject()
    {
        var index = _projectList.SelectedIndex;
        if (index < 0 || index >= _options.Projects.Count) return;

        var nome = _options.Projects[index].Name;
        var resposta = MessageBox.Show(
            this, $"Remover o projeto '{nome}' da configuracao?\n\nIsto nao apaga workspace, artefatos nem historico.",
            "UnityLocalCI", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (resposta != DialogResult.Yes) return;

        _options.Projects.RemoveAt(index);
        RefreshProjectList();
    }

    // ------------------------------------------------------------------ gravar

    private void Save(bool restart)
    {
        // O PropertyGrid pode estar com uma celula em edicao.
        _schedulerGrid.Refresh();
        _projectGrid.Refresh();

        var problemas = ConfigFile.Save(_configPath, _options, _credentials);

        if (problemas.Count > 0)
        {
            Report("Não gravado:" + Environment.NewLine +
                   string.Join(Environment.NewLine, problemas.Take(4).Select(p => "  - " + p)), problema: true);
            return;
        }

        Report("Gravado em " + _configPath, problema: false);
        RefreshProjectList();

        if (!restart) return;

        _ = Task.Run(async () =>
        {
            var ok = await _controller.RestartAsync();
            BeginInvoke(() =>
            {
                if (ok) Report("Gravado e serviço reiniciado.", problema: false);
                else Report("Gravado, mas o serviço não subiu: " +
                            string.Join(" | ", _controller.StartupErrors), problema: true);
            });
        });
    }

    /// <summary>
    /// A mesma linha diz as duas coisas, entao a cor precisa acompanhar: sucesso
    /// escrito em vermelho ensina o usuario a ignorar o vermelho.
    /// </summary>
    private void Report(string text, bool problema)
    {
        _problems.Text = text;
        _problems.ForeColor = problema ? Theme.Danger : Theme.Success;

        // Escondida quando nao ha o que dizer: uma faixa vazia de 60 pixels
        // entre o conteudo e os botoes so faz a tela parecer desalinhada.
        _problems.Visible = text.Length > 0;
    }

    private void OpenInEditor()
    {
        if (!File.Exists(_configPath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_configPath) { UseShellExecute = true });
    }

    private static PropertyGrid NewPropertyGrid() => new()
    {
        Dock = DockStyle.Fill,
        ToolbarVisible = false,
        PropertySort = PropertySort.NoSort,
        HelpVisible = true,
    };
}

/// <summary>
/// Achata o projeto para o PropertyGrid: sem isto, Repository, Unity e
/// Publishing apareceriam como sub-objetos que o usuario precisa expandir um a
/// um, e os campos herdados de Defaults nao teriam explicacao nenhuma.
/// </summary>
public sealed class ProjectView
{
    private readonly ProjectOptions _project;
    private readonly RepositoryOptions _repository;
    private readonly PublishingOptions _publishing;

    public ProjectView(ProjectOptions project)
    {
        _project = project;

        // Guardados em campos nao-anulaveis: as sobrescritas por projeto sao
        // opcionais na configuracao, mas a tela sempre tem onde escrever.
        _repository = _project.Repository ??= new RepositoryOptions();
        _publishing = _project.Publishing ??= new PublishingOptions();
    }

    [System.ComponentModel.Category("Projeto")]
    [System.ComponentModel.Description("Identifica a fila, o estado e aparece nos arquivos de status. Precisa ser unico.")]
    public string Name
    {
        get => _project.Name;
        set => _project.Name = value;
    }

    [System.ComponentModel.Category("Projeto")]
    [System.ComponentModel.Description("Desligado, o projeto fica na configuracao mas nao e observado nem construido.")]
    public bool Enabled
    {
        get => _project.Enabled;
        set => _project.Enabled = value;
    }

    [System.ComponentModel.Category("Repositorio")]
    public string Url
    {
        get => _repository.Url;
        set => _repository.Url = value;
    }

    [System.ComponentModel.Category("Repositorio")]
    [System.ComponentModel.Description("Branch observada. A build dispara quando o HEAD dela muda.")]
    public string Branch
    {
        get => _repository.Branch;
        set => _repository.Branch = value;
    }

    [System.ComponentModel.Category("Repositorio")]
    [System.ComponentModel.Description("Clone dedicado e permanente. Dois projetos nunca podem compartilhar o mesmo caminho: o Unity trava a Library do diretorio.")]
    public string WorkspacePath
    {
        get => _repository.WorkspacePath;
        set => _repository.WorkspacePath = value;
    }

    [System.ComponentModel.Category("Repositorio")]
    [System.ComponentModel.Description("NOME da credencial no Windows Credential Manager, nunca o PAT. Grave o valor com tools\\set-secrets.ps1.")]
    public string? PatCredentialName
    {
        get => _repository.PatCredentialName;
        set => _repository.PatCredentialName = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [System.ComponentModel.Category("Unity")]
    [System.ComponentModel.Description("Versao exata do editor. Vazio herda de Defaults. Nao e adivinhada: buildar na versao errada produz um artefato que parece certo e nao e.")]
    public string? EditorVersion
    {
        get => _project.Unity?.EditorVersion;
        set
        {
            _project.Unity ??= new UnityOptions();
            _project.Unity.EditorVersion = string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    [System.ComponentModel.Category("Unity")]
    [System.ComponentModel.Description("Vazio herda de Defaults (WebGL).")]
    public string? BuildTarget
    {
        get => _project.Unity?.BuildTarget;
        set
        {
            _project.Unity ??= new UnityOptions();
            _project.Unity.BuildTarget = string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    [System.ComponentModel.Category("Publicacao")]
    [System.ComponentModel.Description("Pasta onde o time pega o zip.")]
    public string? ArtifactFolder
    {
        get => _publishing.ArtifactFolder;
        set => _publishing.ArtifactFolder = value;
    }

    [System.ComponentModel.Category("Publicacao")]
    [System.ComponentModel.Description("Tocar este arquivo enfileira uma build do HEAD atual. O servico o apaga ao consumir.")]
    public string? ManualTriggerFile
    {
        get => _project.ManualTriggerFile;
        set => _project.ManualTriggerFile = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public override string ToString() => _project.Name;
}
