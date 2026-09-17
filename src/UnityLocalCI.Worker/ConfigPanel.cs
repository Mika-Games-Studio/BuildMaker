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

        // Duas fileiras: a coluna da lista e estreita, e numa fileira so o
        // ultimo botao saia da area visivel sem deixar rastro.
        var listaBotoes = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 82,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = Theme.Surface,
        };

        var vincular = new PillButton("Vincular projeto Unity", ButtonKind.Primary)
        {
            Width = 168,
            Backdrop = Theme.Surface,
        };
        var adicionar = new PillButton("Vazio") { Width = 76, Backdrop = Theme.Surface, Margin = new Padding(0, 6, 8, 0) };
        var remover = new PillButton("Remover", ButtonKind.Ghost) { Width = 92, Backdrop = Theme.Surface, Margin = new Padding(0, 6, 8, 0) };

        vincular.Click += (_, _) => LinkUnityProject();
        adicionar.Click += (_, _) => AddProject();
        remover.Click += (_, _) => RemoveProject();
        listaBotoes.Controls.AddRange([vincular, adicionar, remover]);

        _projectList.SelectedIndexChanged += (_, _) => ShowSelectedProject();

        // Escolher uma pasta pode preencher outro campo — a versao do editor sai
        // do workspace. Sem este refresh, o valor novo só apareceria ao trocar
        // de projeto e voltar.
        _projectGrid.PropertyValueChanged += (_, _) => { _projectGrid.Refresh(); RefreshProjectList(); };

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

        _schedulerGrid.SelectedObject = new GeneralView(_options);
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

    /// <summary>
    /// Cadastra um projeto a partir de uma pasta que ja existe na maquina.
    ///
    /// A pasta escolhida serve so para LER: dela saem a URL, a branch e a versao
    /// do editor. O workspace do CI e outro, proprio, porque o pipeline apaga o
    /// que nao esta commitado antes de cada build — apontar para a pasta de
    /// trabalho de alguem destruiria o que estivesse em andamento.
    /// </summary>
    private void LinkUnityProject()
    {
        using var dialog = new FolderBrowserDialog
        {
            UseDescriptionForTitle = true,
            Description = "Escolha a pasta do projeto Unity (a que tem Assets e ProjectSettings)",
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var rascunho = ProjectDraft.FromFolder(dialog.SelectedPath, _options.Projects);

        if (!rascunho.Recognized)
        {
            MessageBox.Show(
                this,
                "Essa pasta não tem ProjectSettings\\ProjectVersion.txt nem um repositório Git." +
                Environment.NewLine + Environment.NewLine +
                "Escolha a pasta raiz do projeto Unity, ou use 'Vazio' para preencher tudo à mão.",
                "UnityLocalCI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var projeto = rascunho.Project;

        _options.Projects.Add(projeto);
        RefreshProjectList();
        _projectList.SelectedIndex = _options.Projects.Count - 1;

        var resumo = new List<string>
        {
            "Nome: " + projeto.Name,
            "Repositório: " + (rascunho.Repository?.Url is { Length: > 0 } url ? url : "(não encontrado — preencha)"),
            "Branch: " + (rascunho.Repository?.Branch ?? "(HEAD solto — confira)"),
            "Versão do Unity: " + (rascunho.EditorVersion ?? "(não encontrada — preencha)"),
            "Workspace do CI: " + projeto.Repository.WorkspacePath,
        };

        MessageBox.Show(
            this,
            "Projeto vinculado:" + Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine, resumo.Select(l => "  " + l)) + Environment.NewLine + Environment.NewLine +
            "O CI não constrói dentro da pasta que você escolheu: ele clona no workspace acima." +
            Environment.NewLine + Environment.NewLine +
            "Falta escolher a pasta de destino (ArtifactFolder) e marcar Enabled como True. Depois, Salvar e reiniciar.",
            "UnityLocalCI", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        PropertySort = PropertySort.Categorized,
        HelpVisible = true,
    };
}
