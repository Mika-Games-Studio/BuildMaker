using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using UnityLocalCI.Core.Secrets;

namespace UnityLocalCI.App;

/// <summary>
/// Conectar ao GitHub pelo navegador, sem ninguem criar token a mao.
///
/// Dois caminhos, e os dois terminam com o token dentro do cofre do Windows:
///
///   - o fluxo de dispositivo do GitHub, que mostra um codigo e abre o
///     navegador. Precisa do Client ID de um OAuth App — que e publico, porque
///     este fluxo nao usa client secret;
///   - a sessao do GitHub CLI, quando ele ja esta instalado e conectado nesta
///     maquina. E o caminho de zero configuracao.
///
/// O token nunca aparece na tela, no log nem em arquivo.
/// </summary>
internal sealed class GitHubSignInForm : Form
{
    private readonly ICredentialStore _credentials;
    private readonly string? _clientId;

    private readonly Label _codigo = new()
    {
        Dock = DockStyle.Top,
        Height = 54,
        Font = new Font(Theme.Mono.FontFamily, 22f, FontStyle.Bold),
        ForeColor = Theme.Accent,
        TextAlign = ContentAlignment.MiddleCenter,
    };

    private readonly Label _explicacao = new()
    {
        Dock = DockStyle.Top,
        Height = 78,
        ForeColor = Theme.TextMuted,
        Padding = new Padding(2, 4, 2, 4),
    };

    private readonly Label _situacao = new()
    {
        Dock = DockStyle.Bottom,
        Height = 44,
        ForeColor = Theme.TextMuted,
        Padding = new Padding(2, 6, 2, 2),
    };

    private readonly TextBox _clientIdBox = new() { Dock = DockStyle.Top, Height = 26 };

    private readonly PillButton _abrir;
    private readonly PillButton _usarGh;
    private readonly PillButton _usarGit;
    private readonly PillButton _fechar = new("Cancelar", ButtonKind.Ghost) { Width = 110 };

    private CancellationTokenSource? _cancelamento;

    /// <summary>Token obtido, ou nulo se a janela foi fechada antes.</summary>
    public string? Token { get; private set; }

    /// <summary>Client ID que o usuario digitou aqui, para ser gravado na configuracao.</summary>
    public string? ClientIdInformado { get; private set; }

    public GitHubSignInForm(ICredentialStore credentials, string? clientId)
    {
        _credentials = credentials;
        _clientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();

        Text = "Conectar ao GitHub";
        Icon = AppIcon.Load();
        Width = 560;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Canvas;
        Font = Theme.Ui;
        Padding = new Padding(18, 14, 18, 14);

        _abrir = new PillButton("Abrir o GitHub e copiar o código", ButtonKind.Primary) { Width = 250 };
        _usarGh = new PillButton("Usar a conta do GitHub CLI", ButtonKind.Default) { Width = 210 };
        _usarGit = new PillButton("Usar a conta do Git desta máquina", ButtonKind.Default) { Width = 250 };

        BuildLayout();

        Theme.Apply(this);
        _explicacao.ForeColor = Theme.TextMuted;
        _situacao.ForeColor = Theme.TextMuted;
        _codigo.ForeColor = Theme.Accent;
    }

    private void BuildLayout()
    {
        var acoes = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Canvas,
        };

        _fechar.Click += (_, _) => Close();
        _abrir.Click += (_, _) => AbrirNavegador();
        _usarGh.Click += (_, _) => UsarGitHubCli();
        _usarGit.Click += (_, _) => UsarContaDoGit();

        acoes.Controls.AddRange([_fechar, _abrir]);

        var topo = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
        topo.Controls.Add(_explicacao);
        topo.Controls.Add(_codigo);

        Controls.Add(topo);
        Controls.Add(_situacao);
        Controls.Add(acoes);

        // Os atalhos ficam embaixo do fluxo do navegador, e nao no lugar dele:
        // eles dependem de a maquina ja ter uma conta guardada, o que nem sempre
        // e verdade na maquina de build.
        var atalhos = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            BackColor = Theme.Canvas,
            WrapContents = false,
        };

        atalhos.Controls.Add(_usarGit);
        if (GitHubCli.Available) atalhos.Controls.Add(_usarGh);

        Controls.Add(atalhos);

        if (_clientId is null) MontarPedidoDeClientId();
    }

    /// <summary>
    /// Sem OAuth App configurado nao ha fluxo de dispositivo. Em vez de uma
    /// mensagem de erro, a janela mostra o que fazer — e o caminho do GitHub CLI
    /// continua disponivel ao lado.
    /// </summary>
    private void MontarPedidoDeClientId()
    {
        _codigo.Text = "";
        _codigo.Height = 10;

        _explicacao.Text =
            "Para entrar pelo navegador, este programa precisa do Client ID de um OAuth App do GitHub — " +
            "uma vez por empresa. Crie em github.com/settings/applications/new, marque 'Enable Device Flow' " +
            "e cole o Client ID abaixo. O Client ID é público: ele não é um segredo.";

        var criar = new PillButton("Criar o OAuth App no GitHub", ButtonKind.Default) { Width = 220, Dock = DockStyle.Top };
        criar.Click += (_, _) => Abrir("https://github.com/settings/applications/new");

        var usar = new PillButton("Usar este Client ID", ButtonKind.Primary) { Width = 180, Dock = DockStyle.Top };
        usar.Click += (_, _) => ComecarComClientId(_clientIdBox.Text);

        var painel = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = Theme.Canvas };
        painel.Controls.Add(usar);
        painel.Controls.Add(_clientIdBox);
        painel.Controls.Add(criar);

        Controls.Add(painel);
        _abrir.Enabled = false;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.ApplyTitleBar(this);

        if (_clientId is not null) ComecarComClientId(_clientId);
    }

    // --------------------------------------------------------- fluxo do navegador

    private DeviceLogin? _login;

    private void ComecarComClientId(string? clientId)
    {
        clientId = clientId?.Trim();
        if (string.IsNullOrEmpty(clientId))
        {
            Situacao("Cole o Client ID do OAuth App.", problema: true);
            return;
        }

        ClientIdInformado = clientId;
        Situacao("Pedindo um código ao GitHub...", problema: false);

        _cancelamento = new CancellationTokenSource();
        var ct = _cancelamento.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var fluxo = new GitHubDeviceFlow(http);

                var login = await fluxo.StartAsync(clientId, ct).ConfigureAwait(false);
                NaJanela(() =>
                {
                    _login = login;
                    _codigo.Text = login.UserCode;
                    _codigo.Height = 54;
                    _abrir.Enabled = true;
                    _explicacao.Text =
                        "Abra o endereço abaixo e digite este código para autorizar. Esta janela reconhece a " +
                        "autorização sozinha e guarda o acesso no cofre do Windows — o token não passa por " +
                        "arquivo nem aparece na tela.\n\n" + login.VerificationUri;

                    Situacao("Esperando a autorização no navegador...", problema: false);
                });

                var token = await fluxo.CompleteAsync(clientId, login, ct).ConfigureAwait(false);

                NaJanela(() => Concluir(token));
            }
            catch (OperationCanceledException) { /* janela fechada */ }
            catch (GitHubSignInException exception)
            {
                NaJanela(() => Situacao(exception.Message, problema: true));
            }
            catch (Exception exception)
            {
                NaJanela(() => Situacao("Falha ao falar com o GitHub: " + exception.Message, problema: true));
            }
        }, ct);
    }

    private void AbrirNavegador()
    {
        if (_login is null) return;

        try { Clipboard.SetDataObject(_login.UserCode, copy: true, retryTimes: 4, retryDelay: 40); }
        catch (ExternalException) { /* area de transferencia ocupada */ }

        Abrir(_login.VerificationUri);
        Situacao("Código copiado. Cole no navegador e autorize.", problema: false);
    }

    // ------------------------------------------- conta ja guardada na maquina

    /// <summary>
    /// A conta que o proprio git usa aqui — a mesma que o GitHub Desktop grava
    /// quando alguem entra por ele. Zero configuracao quando ja existe.
    /// </summary>
    private void UsarContaDoGit()
    {
        Situacao("Perguntando ao Git desta máquina...", problema: false);
        _usarGit.Enabled = false;

        _ = Task.Run(() =>
        {
            var token = GitCredentials.ReadToken();

            NaJanela(() =>
            {
                _usarGit.Enabled = true;

                if (token is null)
                {
                    Situacao(
                        "O Git desta máquina não tem conta do GitHub guardada. Entre pelo navegador, ou clone " +
                        "algo por HTTPS uma vez para o Windows guardar a credencial.",
                        problema: true);
                    return;
                }

                Concluir(token);
            });
        });
    }

    // ------------------------------------------------------------- GitHub CLI

    private void UsarGitHubCli()
    {
        Situacao("Lendo a sessão do GitHub CLI...", problema: false);
        _usarGh.Enabled = false;

        _ = Task.Run(() =>
        {
            var token = GitHubCli.ReadToken();

            NaJanela(() =>
            {
                _usarGh.Enabled = true;

                if (token is null)
                {
                    Situacao(
                        "O GitHub CLI está instalado mas não tem sessão. Rode 'gh auth login' uma vez e tente de novo.",
                        problema: true);
                    return;
                }

                Concluir(token);
            });
        });
    }

    // ---------------------------------------------------------------- comum

    private void Concluir(string token)
    {
        try
        {
            _credentials.Write(GitHubConnection.CredentialName, token);
        }
        catch (Exception exception)
        {
            Situacao("Conectado, mas não foi possível gravar no cofre: " + exception.Message, problema: true);
            return;
        }

        Token = token;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Situacao(string texto, bool problema)
    {
        _situacao.Text = texto;
        _situacao.ForeColor = problema ? Theme.Danger : Theme.TextMuted;
    }

    private void NaJanela(Action acao)
    {
        if (!IsHandleCreated) return;

        try { BeginInvoke(acao); }
        catch (ObjectDisposedException) { /* janela fechada */ }
        catch (InvalidOperationException) { /* handle indo embora */ }
    }

    private static void Abrir(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Sem navegador associado: o endereco continua escrito na tela.
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cancelamento?.Cancel();
        _cancelamento?.Dispose();
        base.OnFormClosed(e);
    }
}
