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
    private readonly BranchCatalog _branches;

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
        _branches = new BranchCatalog(_credentials);

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

        // Tres fileiras: a coluna da lista e estreita, e o botao que nao
        // coubesse sairia da area visivel sem deixar rastro.
        var listaBotoes = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 126,
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

        var conectar = new PillButton("Conectar ao GitHub", ButtonKind.Default)
        {
            Width = 168,
            Backdrop = Theme.Surface,
            Margin = new Padding(0, 6, 8, 0),
        };

        vincular.Click += (_, _) => LinkUnityProject();
        adicionar.Click += (_, _) => AddProject();
        remover.Click += (_, _) => RemoveProject();
        conectar.Click += (_, _) => ConnectToGitHub();
        listaBotoes.Controls.AddRange([conectar, vincular, adicionar, remover]);

        _projectList.SelectedIndexChanged += (_, _) => ShowSelectedProject();

        // Escolher uma pasta pode preencher outro campo — a versao do editor sai
        // do workspace. Aqui o Refresh e seguro: este evento so dispara DEPOIS
        // de o valor ter sido confirmado.
        _projectGrid.PropertyValueChanged += (_, e) =>
        {
            _projectGrid.Refresh();
            RefreshProjectList();

            // Trocou a URL ou a credencial: a lista de branches da anterior nao
            // vale mais.
            var propriedade = e.ChangedItem?.PropertyDescriptor?.Name;
            if (propriedade is nameof(ProjectView.Url) or nameof(ProjectView.PatCredentialName)
                && SelectedProject() is { } projeto)
            {
                _branches.EnsureLoaded(projeto, _options.Defaults, force: true);
            }
        };

        _branches.Updated += OnBranchesLoaded;

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

        if (index < 0 || index >= _options.Projects.Count)
        {
            _projectGrid.SelectedObject = null;
            return;
        }

        var project = _options.Projects[index];
        _projectGrid.SelectedObject = new ProjectView(project, _branches, _options.Defaults);

        // A lista de branches e buscada em segundo plano; quando o usuario abrir
        // o dropdown, ela ja estara la. Nada aqui espera pela rede.
        _branches.EnsureLoaded(project, _options.Defaults);
        ReportBranches(project.Repository?.Url);
    }

    /// <summary>
    /// Diz em que pe esta a lista de branches do repositorio selecionado.
    ///
    /// E chamado tanto ao selecionar o projeto quanto quando a consulta termina:
    /// a carga costuma acabar antes de alguem abrir a pagina de configuracao, e
    /// so o aviso de chegada deixaria a tela muda justamente no caso normal.
    /// </summary>
    private void ReportBranches(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // Nao atropela uma mensagem de gravacao ou de erro, que importam mais —
        // mas substitui livremente o proprio aviso anterior sobre branches.
        if (_problems.Text.Length > 0 && !_branchMessage) return;

        if (_branches.IsLoading(url))
        {
            Report("Consultando as branches de " + url + "...", problema: false, sobreBranches: true);
            return;
        }

        var quantas = _branches.Known(url).Count;

        Report(
            quantas == 0
                ? "Não foi possível listar as branches de " + url +
                  ". Confira a URL e a credencial — dá para digitar o nome da branch à mão."
                : $"{quantas} branch(es) no dropdown do campo Branch.",
            problema: quantas == 0,
            sobreBranches: true);
    }

    /// <summary>
    /// A busca das branches costuma terminar antes de alguem abrir esta pagina —
    /// e ate antes de ela ter um handle, quando o aviso de chegada nao tem para
    /// onde ir. Entao o estado e reavaliado toda vez que a pagina aparece.
    /// </summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        if (Visible) ReportBranches(SelectedProject()?.Repository?.Url);
    }

    /// <summary>
    /// Entrar no GitHub pelo navegador e apontar os projetos para a credencial
    /// resultante.
    ///
    /// O token vai do navegador para o cofre do Windows sem passar por arquivo
    /// nem pela tela; o que fica na configuracao continua sendo so o NOME da
    /// credencial.
    /// </summary>
    private void ConnectToGitHub()
    {
        using var janela = new GitHubSignInForm(_credentials, _options.GitHub.ClientId);

        if (janela.ShowDialog(this) != DialogResult.OK) return;

        // O Client ID digitado na hora fica gravado, para a proxima conexao
        // nesta maquina ser um clique so.
        if (janela.ClientIdInformado is { Length: > 0 } clientId && clientId != _options.GitHub.ClientId)
            _options.GitHub.ClientId = clientId;

        // A conexao e da maquina, nao de cada jogo: ela vai para os padroes, e os
        // projetos herdam. As credenciais por projeto que existiam antes saem do
        // caminho — quem precisar de uma conta diferente num projeto especifico
        // volta a preencher o campo dele.
        _options.Defaults.Repository.PatCredentialName = GitHubConnection.CredentialName;

        var comCredencialPropria = _options.Projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Repository?.PatCredentialName))
            .ToList();

        foreach (var projeto in comCredencialPropria)
            projeto.Repository!.PatCredentialName = null;

        ShowSelectedProject();

        // A lista de branches de todos eles muda agora que ha acesso.
        _branches.Clear();
        if (SelectedProject() is { } atual)
        {
            _branches.EnsureLoaded(atual, _options.Defaults, force: true);
            ReportBranches(atual.Repository?.Url);
        }

        MessageBox.Show(
            this,
            "Conectado ao GitHub." + Environment.NewLine + Environment.NewLine +
            $"O acesso vale para a máquina inteira, na credencial '{GitHubConnection.CredentialName}'. " +
            "Todos os projetos passam a usá-lo — não é preciso configurar por jogo." +
            (comCredencialPropria.Count > 0
                ? Environment.NewLine + Environment.NewLine +
                  $"{comCredencialPropria.Count} projeto(s) tinham credencial própria e passaram a herdar esta."
                : "") +
            Environment.NewLine + Environment.NewLine +
            "Falta salvar para valer.",
            "UnityLocalCI", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private ProjectOptions? SelectedProject()
    {
        var index = _projectList.SelectedIndex;
        return index >= 0 && index < _options.Projects.Count ? _options.Projects[index] : null;
    }

    /// <summary>
    /// Chegou a lista de branches de um repositorio, numa thread do pool.
    ///
    /// A grade nao e atualizada aqui de proposito: o dropdown pergunta a lista
    /// na hora em que e aberto, e um Refresh no meio de uma digitacao jogaria
    /// fora o que estivesse sendo digitado. O que aparece e so um aviso, e so
    /// quando nao ha outra mensagem mais importante na tela.
    /// </summary>
    private void OnBranchesLoaded(string url)
    {
        if (!IsHandleCreated) return;

        try
        {
            BeginInvoke(() =>
            {
                if (_branches.IsLoading(url)) return;
                ReportBranches(url);
            });
        }
        catch (ObjectDisposedException) { /* janela fechando */ }
        catch (InvalidOperationException) { /* handle indo embora */ }
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

    /// <summary>
    /// Projeto em branco. O nome e perguntado aqui porque ele nao e editavel
    /// depois: e ele que identifica a fila, o arquivo de configuracao, o gatilho
    /// e o historico.
    /// </summary>
    private void AddProject()
    {
        var nome = TextPrompt.Ask(
            this,
            "Novo projeto",
            "Nome do projeto. Ele identifica a fila, o arquivo de configuração, o gatilho e o histórico — " +
            "e não muda depois.");

        if (nome is null) return;

        if (_options.Projects.Any(p => string.Equals(p.Name, nome, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                this, $"Já existe um projeto chamado '{nome}'.",
                "UnityLocalCI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _options.Projects.Add(new ProjectOptions
        {
            Name = nome,
            Enabled = false,
            Repository = new RepositoryOptions { Branch = "HML" },
            Unity = new UnityOptions { BuildTarget = BuildTargetConverter.Alvos[0] },
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

    /// <summary>
    /// Confirma o que esta sendo digitado antes de gravar.
    ///
    /// O PropertyGrid so escreve o valor no objeto quando a celula perde o foco.
    /// Aqui havia um Refresh() com a intencao contraria: Refresh recarrega a
    /// grade a partir do objeto e JOGA FORA o que foi digitado. O efeito era
    /// gravar — ou reprovar — o valor antigo, com a tela mostrando o novo.
    ///
    /// Tirar o foco e o que confirma a edicao.
    /// </summary>
    private void CommitPendingEdits()
    {
        var form = FindForm();
        if (form is null) return;

        if (_schedulerGrid.ContainsFocus || _projectGrid.ContainsFocus)
            form.ActiveControl = null;
    }

    private void Save(bool restart)
    {
        CommitPendingEdits();

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
    private void Report(string text, bool problema, bool sobreBranches = false)
    {
        _problems.Text = text;
        _problems.ForeColor = problema ? Theme.Danger : Theme.Success;

        // Escondida quando nao ha o que dizer: uma faixa vazia de 60 pixels
        // entre o conteudo e os botoes so faz a tela parecer desalinhada.
        _problems.Visible = text.Length > 0;

        _branchMessage = sobreBranches;
    }

    /// <summary>
    /// Verdadeiro quando o que esta escrito na linha de mensagens foi posto pela
    /// propria busca de branches. Sem isso, o "Consultando..." bloquearia o
    /// resultado que vem logo depois — a mensagem ficaria parada em "consultando"
    /// para sempre.
    /// </summary>
    private bool _branchMessage;

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
