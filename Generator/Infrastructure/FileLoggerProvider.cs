namespace AhkWin32.Generator.Infrastructure;

using Microsoft.Extensions.Logging;

/// <summary>
/// Minimal file logger provider that writes formatted log lines to a file.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();

    public FileLoggerProvider(string path, LogLevel minLevel)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is { Length: > 0 })
            Directory.CreateDirectory(dir);

        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _minLevel, _lock);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(string category, StreamWriter writer, LogLevel minLevel, object @lock) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string level = logLevel switch
            {
                LogLevel.Trace => "trce",
                LogLevel.Debug => "dbug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                LogLevel.Critical => "crit",
                _ => "????"
            };

            lock (@lock)
            {
                writer.WriteLine($"{timestamp} {level}: {category}[{eventId.Id}] {formatter(state, exception)}");
                if (exception != null)
                    writer.WriteLine(exception.ToString());
            }
        }
    }
}
