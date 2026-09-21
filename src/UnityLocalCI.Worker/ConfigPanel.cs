using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Git;
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

    private readonly Label _githubStatus = new() { AutoSize = false };
    private readonly AccountCard _conta = new();

    /// <summary>Credencial cuja conta ja foi buscada, para nao repetir a consulta.</summary>
    private string? _contaConsultada;
    private CancellationTokenSource? _buscaDaConta;

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
            Height = 84,
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

        tabs.TabPages.Add(BuildGitHubTab());

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

    /// <summary>
    /// A conexao com o GitHub tem aba propria porque ela nao pertence a projeto
    /// nenhum: e o que todos herdam. No meio do cadastro de um projeto, ela
    /// sugeria — errado — que cada jogo tem a sua.
    /// </summary>
    private TabPage BuildGitHubTab()
    {
        var pagina = new TabPage("GitHub") { Padding = new Padding(0, 10, 0, 0) };

        _githubStatus.Dock = DockStyle.Top;
        _githubStatus.Height = 72;
        _githubStatus.Padding = new Padding(2, 4, 2, 8);

        var botoes = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            BackColor = Theme.Surface,
        };

        var conectar = new PillButton("Conectar ao GitHub", ButtonKind.Primary) { Width = 168, Backdrop = Theme.Surface };
        var testar = new PillButton("Testar acesso", ButtonKind.Default) { Width = 130, Backdrop = Theme.Surface };
        var outraConta = new PillButton("Entrar com outra conta", ButtonKind.Default) { Width = 180, Backdrop = Theme.Surface };
        var esquecer = new PillButton("Desconectar", ButtonKind.Ghost) { Width = 120, Backdrop = Theme.Surface };

        conectar.Click += (_, _) => ConnectToGitHub(conectar);
        testar.Click += (_, _) => TestGitHubAccess(testar);
        outraConta.Click += (_, _) => SignInToGitHub();
        esquecer.Click += (_, _) => DisconnectFromGitHub();

        botoes.Controls.AddRange([conectar, testar, outraConta, esquecer]);

        _conta.Dock = DockStyle.Top;
        _conta.Margin = new Padding(0, 0, 0, 10);

        // Nada aqui e editavel de proposito. O nome da credencial e escolha do
        // programa, e o Client ID so importa dentro do botao, quando sobra o
        // navegador — campos para os dois convidavam a mexer no que a conexao
        // ja resolve sozinha.
        var explicacao = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextMuted,
            Padding = new Padding(2, 12, 2, 2),
            Text =
                "O acesso fica no Gerenciador de Credenciais do Windows, nunca na configuração — o arquivo " +
                "guarda só o nome da credencial." + Environment.NewLine + Environment.NewLine +
                "'Conectar ao GitHub' procura primeiro uma conta que esta máquina já tenha: o acesso guardado " +
                "aqui, a conta que o Git usa (a mesma do GitHub Desktop) e a sessão do GitHub CLI. Só quando " +
                "não encontra nenhuma é que ele abre o navegador para você autorizar." + Environment.NewLine +
                Environment.NewLine +
                "'Testar acesso' pergunta ao servidor, projeto por projeto, se a conexão alcança o repositório: " +
                "a credencial existir no cofre não significa que ela tem permissão lá.",
        };

        var cartao = WrapInCard(explicacao);
        cartao.Controls.Add(_conta);
        cartao.Controls.Add(botoes);
        cartao.Controls.Add(_githubStatus);

        pagina.Controls.Add(cartao);
        return pagina;
    }

    /// <summary>
    /// Em que pe esta a conexao da maquina. Mostra tambem quantos projetos a
    /// herdam e quantos tem excecao: sem isso, "conectado" nao responde a
    /// pergunta que importa, que e se os projetos vao conseguir clonar.
    /// </summary>
    private void RefreshGitHubStatus()
    {
        var nome = _options.Defaults.Repository.PatCredentialName;

        var herdam = _options.Projects.Count(p => string.IsNullOrWhiteSpace(p.Repository?.PatCredentialName));
        var excecoes = _options.Projects.Count - herdam;

        var rodape = $"{herdam} projeto(s) herdam esta conexão" +
                     (excecoes > 0 ? $", {excecoes} com credencial própria." : ".");

        if (string.IsNullOrWhiteSpace(nome))
        {
            _githubStatus.ForeColor = Theme.Warning;
            _githubStatus.Text =
                "Sem conexão. Clique em 'Conectar ao GitHub': ele procura primeiro a conta que o Git ou o " +
                "GitHub CLI desta máquina já guardaram, e só abre o navegador se não achar nada." +
                Environment.NewLine + rodape;

            _contaConsultada = null;
            _conta.Mostrar("Nenhuma conta conectada", "Clique em 'Conectar ao GitHub'.", null, null, conectado: false);
            return;
        }

        var existe = false;
        try { existe = _credentials.Exists(nome); }
        catch (InvalidOperationException) { /* cofre indisponivel: tratado como ausente */ }

        _githubStatus.ForeColor = existe ? Theme.Success : Theme.Danger;
        _githubStatus.Text = existe
            ? $"Conectado. O acesso está guardado no cofre do Windows como '{nome}'." + Environment.NewLine + rodape
            : $"A configuração aponta para '{nome}', que não existe no cofre do Windows. " +
              "Conecte de novo." + Environment.NewLine + rodape;

        if (!existe)
        {
            _contaConsultada = null;
            _conta.Mostrar("Credencial não encontrada", $"'{nome}' não existe no cofre do Windows.", null, null,
                conectado: false);
            return;
        }

        LoadGitHubAccount(nome, forcar: false);
    }

    /// <summary>
    /// Quem e o dono do token guardado, com foto e @.
    ///
    /// E confirmacao visual: "conectado" nao diz se a conta e a do time ou uma
    /// pessoal esquecida nesta maquina. De quebra, uma resposta negativa do
    /// GitHub aparece aqui — e melhor descobrir que o token nao vale mais nesta
    /// tela do que no meio de uma build.
    /// </summary>
    private void LoadGitHubAccount(string credencial, bool forcar)
    {
        if (!forcar && string.Equals(_contaConsultada, credencial, StringComparison.OrdinalIgnoreCase)) return;

        _contaConsultada = credencial;

        _buscaDaConta?.Cancel();
        _buscaDaConta?.Dispose();
        _buscaDaConta = new CancellationTokenSource();
        var ct = _buscaDaConta.Token;

        _conta.Mostrar("Buscando a conta no GitHub...", $"Credencial: {credencial}", null, null, conectado: false);

        _ = Task.Run(async () =>
        {
            GitHubAccount? conta = null;
            byte[]? retrato = null;
            var semResposta = false;

            try
            {
                var token = _credentials.Read(credencial);

                if (!string.IsNullOrWhiteSpace(token))
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var perfil = new GitHubProfile(http);

                    conta = await perfil.ReadAsync(token, ct).ConfigureAwait(false);

                    if (conta?.AvatarUrl is { Length: > 0 } endereco)
                        retrato = await perfil.ReadAvatarAsync(endereco, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                // Sem rede, ou o GitHub demorou demais. Nao e a mesma coisa que
                // token recusado, e dizer "nao reconheceu" aqui seria acusar uma
                // credencial boa.
                semResposta = true;
            }
            catch (InvalidOperationException)
            {
                // Cofre indisponivel: a tela continua dizendo o que sabe.
                semResposta = true;
            }

            if (ct.IsCancellationRequested) return;

            NoFormulario(() => ShowGitHubAccount(credencial, conta, retrato, semResposta));
        }, ct);
    }

    private void ShowGitHubAccount(string credencial, GitHubAccount? conta, byte[]? retrato, bool semResposta)
    {
        if (conta is null && semResposta)
        {
            _conta.Mostrar(
                "Sem resposta do GitHub",
                "Não deu para confirmar de quem é o acesso guardado — provavelmente falta rede.",
                $"Credencial no cofre do Windows: {credencial}",
                null,
                conectado: false);

            // Sem resposta nao e resposta: na proxima vez que a tela aparecer,
            // pergunta de novo.
            _contaConsultada = null;
            return;
        }

        if (conta is null)
        {
            _conta.Mostrar(
                "Conectado, mas o GitHub não reconheceu este acesso",
                $"O que está guardado em '{credencial}' pode ter expirado ou sido revogado.",
                "Use 'Entrar com outra conta' para conectar de novo.",
                null,
                conectado: false);
            return;
        }

        _conta.Mostrar(
            conta.Display,
            "@" + conta.Login,
            $"Credencial no cofre do Windows: {credencial}",
            Retrato(retrato),
            conectado: true);
    }

    /// <summary>
    /// Bytes viram bitmap proprio: com Image.FromStream o fluxo precisa viver
    /// tanto quanto a imagem, e um fluxo fechado vira erro generico do GDI+ na
    /// primeira repintura.
    /// </summary>
    private static Image? Retrato(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            using var fluxo = new MemoryStream(bytes);
            using var original = Image.FromStream(fluxo);
            return new Bitmap(original);
        }
        catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException)
        {
            // Nao era imagem. A silhueta resolve.
            return null;
        }
    }

    /// <summary>
    /// Pergunta ao servidor, projeto por projeto, se a conexao atual da acesso.
    /// E a unica resposta que vale: credencial existir no cofre nao significa
    /// que ela alcanca aquele repositorio.
    /// </summary>
    private void TestGitHubAccess(PillButton botao)
    {
        var alvos = _options.Projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Repository?.Url))
            .Select(p => (p.Name, Url: p.Repository!.Url, Credencial: ProjectResolver.ResolveCredential(p, _options.Defaults)))
            .ToList();

        if (alvos.Count == 0)
        {
            MessageBox.Show(this, "Nenhum projeto com URL cadastrada para testar.",
                "UnityLocalCI", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        botao.Enabled = false;
        _githubStatus.ForeColor = Theme.TextMuted;
        _githubStatus.Text = "Perguntando ao servidor...";

        _ = Task.Run(async () =>
        {
            var git = new GitClient(
                new ProcessRunner(NullLogger<ProcessRunner>.Instance),
                NullLogger<GitClient>.Instance);

            var linhas = new List<string>();

            foreach (var (nome, url, credencial) in alvos)
            {
                var token = string.IsNullOrWhiteSpace(credencial) ? null : _credentials.Read(credencial);

                try
                {
                    var branches = await git.ListRemoteBranchesAsync(
                        new GitContext { WorkspacePath = "", RepositoryUrl = url, Branch = "", PersonalAccessToken = token },
                        CancellationToken.None).ConfigureAwait(false);

                    linhas.Add($"OK    {nome}: {branches.Count} branch(es).");
                }
                catch (GitCommandException exception)
                {
                    var motivo = exception.StandardError.Split('\n').FirstOrDefault()?.Trim();
                    linhas.Add($"FALHA {nome}: {motivo}");
                }
                catch (Exception exception)
                {
                    linhas.Add($"FALHA {nome}: {exception.Message}");
                }
            }

            NoFormulario(() =>
            {
                botao.Enabled = true;
                RefreshGitHubStatus();

                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, linhas) + Environment.NewLine + Environment.NewLine +
                    "'Write access to repository not granted' quer dizer que o token autenticou mas não tem " +
                    "permissão naquele repositório — não que falte permissão de escrita.",
                    "Teste de acesso", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        });
    }

    /// <summary>
    /// Esquece a conexao na configuracao. O token continua no cofre do Windows:
    /// apagar credencial que este programa nao criou seria passar por cima de
    /// outra coisa que a use.
    /// </summary>
    private void DisconnectFromGitHub()
    {
        if (string.IsNullOrWhiteSpace(_options.Defaults.Repository.PatCredentialName)) return;

        var resposta = MessageBox.Show(
            this,
            "Os projetos deixam de usar esta conexão e voltam a clonar sem credencial." +
            Environment.NewLine + Environment.NewLine +
            "O token continua guardado no cofre do Windows; para removê-lo de vez, use o Gerenciador de " +
            "Credenciais do Windows.",
            "UnityLocalCI", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (resposta != DialogResult.Yes) return;

        _options.Defaults.Repository.PatCredentialName = null;

        RefreshGitHubStatus();
        ShowSelectedProject();
    }

    private void NoFormulario(Action acao)
    {
        if (!IsHandleCreated) return;

        try { BeginInvoke(acao); }
        catch (ObjectDisposedException) { /* janela fechando */ }
        catch (InvalidOperationException) { /* handle indo embora */ }
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
        RefreshGitHubStatus();
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

        if (!Visible) return;

        ReportBranches(SelectedProject()?.Repository?.Url);

        // A consulta da conta pode ter ficado sem resposta — falta de rede na
        // hora em que a janela abriu, por exemplo. Aqui ela ganha outra chance.
        if (_contaConsultada is null) RefreshGitHubStatus();
    }

    /// <summary>
    /// O botao que resolve o caso normal: olha primeiro o que esta maquina ja
    /// tem e so manda para o navegador quando nao ha nada.
    ///
    /// A ordem importa. Quem ja clonou por HTTPS aqui, ja usa o GitHub Desktop
    /// ou ja rodou 'gh auth login' tem um token guardado; abrir o navegador
    /// nesse caso seria pedir de novo o que ja esta na mao — e ainda obrigaria a
    /// registrar um OAuth App so para isso.
    /// </summary>
    private void ConnectToGitHub(PillButton botao)
    {
        botao.Enabled = false;
        _githubStatus.ForeColor = Theme.TextMuted;
        _githubStatus.Text = "Procurando uma conta do GitHub já guardada nesta máquina...";

        _ = Task.Run(() =>
        {
            ContaEncontrada? conta = null;
            string? falha = null;

            try
            {
                var busca = new GitHubDiscovery(_credentials);
                conta = busca.Procurar();
                if (conta is not null) busca.Guardar(conta);
            }
            catch (Exception exception)
            {
                falha = exception.Message;
            }

            NoFormulario(() =>
            {
                botao.Enabled = true;

                if (falha is not null)
                {
                    _githubStatus.ForeColor = Theme.Danger;
                    _githubStatus.Text = "Não foi possível guardar o acesso no cofre do Windows: " + falha;
                    return;
                }

                // Nada guardado nesta maquina: agora sim, o caminho do navegador.
                if (conta is null)
                {
                    RefreshGitHubStatus();
                    SignInToGitHub();
                    return;
                }

                AdoptConnection(conta.Origem);
            });
        });
    }

    /// <summary>
    /// Entrar no GitHub pela janela — navegador, GitHub CLI ou a conta do Git.
    ///
    /// O token vai para o cofre do Windows sem passar por arquivo nem pela tela;
    /// o que fica na configuracao continua sendo so o NOME da credencial.
    /// </summary>
    private void SignInToGitHub()
    {
        using var janela = new GitHubSignInForm(
            _credentials,
            GitHubConnection.ClientIdEmVigor(_options.GitHub.ClientId));

        if (janela.ShowDialog(this) != DialogResult.OK) return;

        // O Client ID digitado na hora fica gravado, para a proxima conexao
        // nesta maquina ser um clique so.
        if (janela.ClientIdInformado is { Length: > 0 } clientId && clientId != _options.GitHub.ClientId)
            _options.GitHub.ClientId = clientId;

        AdoptConnection("a conta que você autorizou");
    }

    /// <summary>
    /// Aponta a maquina inteira para a credencial recem-guardada.
    ///
    /// A conexao e da maquina, nao de cada jogo: ela vai para os padroes e os
    /// projetos herdam. As credenciais por projeto que existiam antes saem do
    /// caminho — quem precisar de outra conta num projeto especifico volta a
    /// preencher o campo dele.
    /// </summary>
    private void AdoptConnection(string origem)
    {
        _options.Defaults.Repository.PatCredentialName = GitHubConnection.CredentialName;

        var comCredencialPropria = _options.Projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Repository?.PatCredentialName))
            .ToList();

        foreach (var projeto in comCredencialPropria)
            projeto.Repository!.PatCredentialName = null;

        // Pode ser outra conta guardada sob o mesmo nome: a consulta anterior
        // nao vale mais, e a foto na tela seria a da conta antiga.
        _contaConsultada = null;

        RefreshGitHubStatus();
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
            $"Conectado ao GitHub usando {origem}." + Environment.NewLine + Environment.NewLine +
            $"O acesso vale para a máquina inteira, na credencial '{GitHubConnection.CredentialName}'. " +
            "Todos os projetos passam a usá-lo — não é preciso configurar por jogo." +
            (comCredencialPropria.Count > 0
                ? Environment.NewLine + Environment.NewLine +
                  $"{comCredencialPropria.Count} projeto(s) tinham credencial própria e passaram a herdar esta."
                : "") +
            Environment.NewLine + Environment.NewLine +
            "Use 'Testar acesso' para confirmar no servidor, e salve para valer.",
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
