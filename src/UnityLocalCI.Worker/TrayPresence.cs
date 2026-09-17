using UnityLocalCI.Core.Hosting;

namespace UnityLocalCI.App;

/// <summary>
/// Presenca na bandeja do Windows.
///
/// Fechar a janela pelo X apenas a esconde: o servico continua observando os
/// repositorios, que e o que se espera de um CI. Clicar no icone traz a janela
/// de volta, e sair de verdade so pelo menu do botao direito — com aviso, porque
/// sair para as builds.
/// </summary>
internal sealed class TrayPresence : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Form _form;
    private readonly HostController _controller;
    private readonly ToolStripMenuItem _startWithWindows;

    private bool _explainedHiding;

    public TrayPresence(Form form, HostController controller)
    {
        _form = form;
        _controller = controller;

        _startWithWindows = new ToolStripMenuItem("Iniciar com o Windows")
        {
            CheckOnClick = true,
            Checked = StartupRegistration.IsEnabled,
            Enabled = StartupRegistration.IsSupported,
        };
        _startWithWindows.Click += (_, _) => ToggleStartup();

        if (!StartupRegistration.IsSupported)
            _startWithWindows.ToolTipText = "Disponivel no executavel instalado, nao em 'dotnet run'.";

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => Show());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Exit());

        _icon = new NotifyIcon
        {
            Icon = AppIcon.LoadForTray(),
            Text = "UnityLocalCI",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Clique simples e duplo abrem: e o que a maioria tenta primeiro, e um
        // icone que so responde a duplo clique passa por travado.
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Show(); };
        _icon.DoubleClick += (_, _) => Show();

        _form.Resize += (_, _) =>
        {
            if (_form.WindowState == FormWindowState.Minimized) HideToTray();
        };

        _controller.StateChanged += UpdateTooltip;
        UpdateTooltip();
    }

    /// <summary>
    /// Chamado pela janela quando o usuario fecha no X. Na primeira vez explica
    /// que o programa continua rodando: um app que some da barra de tarefas sem
    /// dizer nada parece ter sido encerrado.
    /// </summary>
    public void HideToTray()
    {
        _form.Hide();

        if (_explainedHiding) return;
        _explainedHiding = true;

        _icon.BalloonTipTitle = "UnityLocalCI continua em execucao";
        _icon.BalloonTipText = "As builds seguem sendo disparadas. Clique no icone para abrir, " +
                              "ou use o botao direito para sair.";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    private void Show()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void ToggleStartup()
    {
        var error = StartupRegistration.Set(_startWithWindows.Checked);
        if (error is null) return;

        // Volta o estado do menu: mostrar marcado algo que nao foi gravado seria
        // pior que a falha em si.
        _startWithWindows.Checked = !_startWithWindows.Checked;

        MessageBox.Show(
            _form,
            "Nao foi possivel alterar o inicio automatico:" + Environment.NewLine + error,
            "UnityLocalCI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void Exit()
    {
        if (_controller.State == HostState.Rodando)
        {
            var resposta = MessageBox.Show(
                _form,
                "Sair encerra o serviço: nenhuma build será disparada enquanto o programa estiver fechado." +
                Environment.NewLine + Environment.NewLine +
                "Para o CI rodar sem ninguém logado, registre-o como serviço do Windows com " +
                "tools\\install-service.ps1." + Environment.NewLine + Environment.NewLine +
                "Sair mesmo assim?",
                "UnityLocalCI", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (resposta != DialogResult.Yes) return;
        }

        if (_form is MainForm janela) janela.ExitRequested = true;

        _icon.Visible = false;
        Application.Exit();
    }

    private void UpdateTooltip()
    {
        var texto = _controller.State switch
        {
            HostState.Rodando => "UnityLocalCI — em execução",
            HostState.Iniciando => "UnityLocalCI — iniciando",
            HostState.Parado => "UnityLocalCI — parado",
            _ => "UnityLocalCI — falhou ao iniciar",
        };

        // O NotifyIcon trunca em 63 caracteres e lanca acima disso.
        _icon.Text = texto.Length > 63 ? texto[..63] : texto;
    }

    public void Dispose()
    {
        _controller.StateChanged -= UpdateTooltip;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
