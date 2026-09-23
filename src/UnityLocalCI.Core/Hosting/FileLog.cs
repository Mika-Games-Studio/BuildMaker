using System.Text;
using Microsoft.Extensions.Logging;

namespace UnityLocalCI.Core.Hosting;

/// <summary>
/// O log do servico em arquivo, um por dia.
///
/// Existe porque o <see cref="LiveLog"/> vive so na memoria da janela: quando o
/// programa fecha — de proposito ou nao —, tudo que ele sabia vai junto. Foi
/// exatamente o que aconteceu aqui: o aplicativo encerrava no meio de uma build
/// e nao sobrava uma linha em lugar nenhum para dizer por que.
///
/// Escrita sincrona e com AutoFlush: um log de diagnostico que perde as ultimas
/// linhas justamente quando o processo morre nao serve para nada.
/// </summary>
public sealed class FileLog : IDisposable
{
    private readonly string _pasta;
    private readonly object _gate = new();

    private StreamWriter? _writer;
    private DateOnly _dia;

    public FileLog(string pasta) => _pasta = pasta;

    /// <summary>Nome do arquivo do dia. Publico porque a janela diz onde ele esta.</summary>
    public static string NomeDoDia(DateOnly dia) => $"servico-{dia:yyyy-MM-dd}.log";

    public string CaminhoDeHoje => Path.Combine(_pasta, NomeDoDia(DateOnly.FromDateTime(DateTime.Now)));

    public void Write(string linha)
    {
        lock (_gate)
        {
            try
            {
                Abrir();
                _writer?.WriteLine(linha);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Log que derruba o programa e pior que log nenhum. Disco cheio,
                // pasta sem permissao: o servico continua, sem registro.
                _writer = null;
            }
        }
    }

    private void Abrir()
    {
        var hoje = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && _dia == hoje) return;

        _writer?.Dispose();
        Directory.CreateDirectory(_pasta);

        _dia = hoje;
        _writer = new StreamWriter(
            new FileStream(Path.Combine(_pasta, NomeDoDia(hoje)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            Encoding.UTF8)
        {
            AutoFlush = true,
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>
/// Liga o <see cref="ILogger"/> do host ao <see cref="FileLog"/>, no mesmo
/// formato de colunas que a janela mostra.
/// </summary>
public sealed class FileLogProvider : ILoggerProvider
{
    private readonly FileLog _arquivo;

    public FileLogProvider(FileLog arquivo) => _arquivo = arquivo;

    public ILogger CreateLogger(string categoryName) => new Logger(_arquivo, Encurtar(categoryName));

    /// <summary>
    /// So a ultima parte do nome da classe. 'UnityLocalCI.Core.Watching.GitWatcher'
    /// ocupa metade da linha e nao diz mais que 'GitWatcher'.
    /// </summary>
    private static string Encurtar(string categoria)
    {
        var ponto = categoria.LastIndexOf('.');
        return ponto >= 0 && ponto < categoria.Length - 1 ? categoria[(ponto + 1)..] : categoria;
    }

    public void Dispose() { }

    private sealed class Logger : ILogger
    {
        private readonly FileLog _arquivo;
        private readonly string _categoria;

        public Logger(FileLog arquivo, string categoria)
        {
            _arquivo = arquivo;
            _categoria = categoria;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var mensagem = formatter(state, exception);
            if (exception is not null) mensagem += " | " + exception;

            _arquivo.Write($"{DateTime.Now:HH:mm:ss}  {Nivel(logLevel)}  {_categoria,-22}  {mensagem}");
        }

        private static string Nivel(LogLevel level) => level switch
        {
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "DBG",
        };
    }
}
