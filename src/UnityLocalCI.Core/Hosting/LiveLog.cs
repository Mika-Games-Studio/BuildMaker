using Microsoft.Extensions.Logging;

namespace UnityLocalCI.Core.Hosting;

public sealed record LogLine(DateTimeOffset At, LogLevel Level, string Category, string Message);

/// <summary>
/// Buffer circular das ultimas linhas de log, para a janela mostrar o que o
/// servico esta fazendo agora.
///
/// Existe porque o executavel roda sem console quando abre a janela: sem isto,
/// a unica forma de acompanhar seria abrir o arquivo de log em outro programa.
/// </summary>
public sealed class LiveLog
{
    private const int Capacity = 500;

    private readonly Queue<LogLine> _lines = new();
    private readonly object _gate = new();

    /// <summary>Disparado fora de qualquer lock, para um assinante lento nao travar quem loga.</summary>
    public event Action<LogLine>? LineAdded;

    public void Add(LogLine line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Capacity) _lines.Dequeue();
        }

        LineAdded?.Invoke(line);
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }
}

public sealed class LiveLogProvider : ILoggerProvider
{
    private readonly LiveLog _log;

    public LiveLogProvider(LiveLog log) => _log = log;

    public ILogger CreateLogger(string categoryName) => new LiveLogger(_log, categoryName);

    public void Dispose() { }

    private sealed class LiveLogger : ILogger
    {
        private readonly LiveLog _log;
        private readonly string _category;

        public LiveLogger(LiveLog log, string category)
        {
            _log = log;
            _category = category;
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

            var message = formatter(state, exception);
            if (exception is not null) message += " — " + exception.Message;

            // So a ultima parte do namespace: a janela e estreita e
            // "UnityLocalCI.Core.Queue.BuildScheduler" nao acrescenta nada.
            var shortCategory = _category[(_category.LastIndexOf('.') + 1)..];

            _log.Add(new LogLine(DateTimeOffset.Now, logLevel, shortCategory, message));
        }
    }
}
