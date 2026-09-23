using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Configuration;

namespace UnityLocalCI.Core.Hosting;

public enum HostState { Parado, Iniciando, Rodando, Falhou }

/// <summary>
/// Dono do ciclo de vida do host interno.
///
/// Os watchers sao criados a partir da configuracao no momento em que o host e
/// montado, entao mudar a lista de projetos exige remontar. Em vez de fingir
/// que da para trocar tudo a quente, esta classe reinicia o host de forma
/// controlada, que e honesto e previsivel.
/// </summary>
public sealed class HostController : IAsyncDisposable
{
    private readonly LiveLog _liveLog;
    private readonly string _configPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IHost? _host;

    public HostController(LiveLog liveLog, string configPath)
    {
        _liveLog = liveLog;
        _configPath = configPath;
        Arquivo = new FileLog(PastaDeLog(configPath));
    }

    /// <summary>
    /// O log em arquivo. Fica aqui, e nao dentro do host, porque ele precisa
    /// sobreviver a parar e subir o host — e porque a janela escreve nele o
    /// motivo de o programa estar encerrando, que acontece com o host ja parado.
    /// </summary>
    public FileLog Arquivo { get; }

    /// <summary>
    /// Onde gravar, lido do proprio JSON antes de qualquer validacao.
    ///
    /// Passar pelo binder aqui seria circular: o log tem de existir para contar
    /// que a configuracao nao carregou. Se o arquivo nao der para ler, cai para
    /// o padrao — que e o mesmo do <c>StateOptions.LogFolder</c>.
    /// </summary>
    private static string PastaDeLog(string configPath)
    {
        try
        {
            var raiz = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(Path.GetFullPath(configPath))!)
                .AddJsonFile(Path.GetFileName(configPath), optional: true)
                .Build();

            var pasta = raiz["State:LogFolder"];
            if (!string.IsNullOrWhiteSpace(pasta)) return pasta;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { }

        return new StateOptions().LogFolder;
    }

    public HostState State { get; private set; } = HostState.Parado;

    /// <summary>Problemas de configuracao que impediram o host de subir.</summary>
    public IReadOnlyList<string> StartupErrors { get; private set; } = Array.Empty<string>();

    public event Action? StateChanged;

    /// <summary>Servicos do host em execucao, ou nulo quando parado.</summary>
    public IServiceProvider? Services => _host?.Services;

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host is not null) return true;

            SetState(HostState.Iniciando);
            StartupErrors = Array.Empty<string>();

            try
            {
                var host = Build();

                // Forca a validacao antes do start, para a configuracao invalida
                // virar uma lista de itens na janela em vez de uma excecao solta.
                _ = host.Services.GetRequiredService<IOptions<CiOptions>>().Value;

                await host.StartAsync(ct).ConfigureAwait(false);

                _host = host;
                SetState(HostState.Rodando);
                return true;
            }
            catch (OptionsValidationException exception)
            {
                StartupErrors = exception.Failures.ToList();
                SetState(HostState.Falhou);
                return false;
            }
            catch (Exception exception)
            {
                StartupErrors = new[] { exception.Message };
                SetState(HostState.Falhou);
                return false;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host is null) return;

            var host = _host;
            _host = null;

            await host.StopAsync(ct).ConfigureAwait(false);
            host.Dispose();

            SetState(HostState.Parado);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct).ConfigureAwait(false);
        return await StartAsync(ct).ConfigureAwait(false);
    }

    private IHost Build()
    {
        // Uma vez, e so na primeira vez: projetos que ainda estejam dentro do
        // appsettings.json passam a ter arquivo proprio.
        ProjectFiles.MigrateFromAppSettings(_configPath);

        var builder = Host.CreateApplicationBuilder();

        builder.Configuration
            .SetBasePath(Path.GetDirectoryName(Path.GetFullPath(_configPath))!)
            .AddJsonFile(Path.GetFileName(_configPath), optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("UNITYLOCALCI_");

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new LiveLogProvider(_liveLog));

        // O arquivo e lido da configuracao crua, e nao das opcoes validadas: o
        // log precisa existir antes de tudo, inclusive para registrar que a
        // configuracao nao passou na validacao.
        builder.Logging.AddProvider(new FileLogProvider(Arquivo));

        builder.Services.AddUnityLocalCI(builder.Configuration, ProjectFiles.FolderFor(_configPath));

        return builder.Build();
    }

    private void SetState(HostState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
