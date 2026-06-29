namespace AhkWin32.Generator.Tests.Support;

using Microsoft.Extensions.Logging;

/// <summary>
/// A trivial <see cref="ILogger{T}"/> that captures every emitted entry so tests can assert on
/// logged behavior (e.g. that <c>CyclicPointerBreaker</c> warns about an unbreakable cluster).
/// </summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public readonly record struct Entry(LogLevel Level, string Message);

    public List<Entry> Entries { get; } = [];

    public IEnumerable<string> MessagesAt(LogLevel level) =>
        Entries.Where(e => e.Level == level).Select(e => e.Message);

    public bool HasMessageAt(LogLevel level, string substring) =>
        MessagesAt(level).Any(m => m.Contains(substring, StringComparison.Ordinal));

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) => Entries.Add(new Entry(logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose() { }
    }
}
