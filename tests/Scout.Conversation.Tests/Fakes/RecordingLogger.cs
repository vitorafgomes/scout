using Microsoft.Extensions.Logging;

namespace Scout.Conversation.Tests.Fakes;

/// <summary>
/// Captures (level, message, exception) per emit so tests can assert log shape:
/// quiet (no exception, no Warning) vs. error (Warning + stack).
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IEnumerable<(LogLevel Level, string Message, Exception? Exception)> EntriesAt(
        LogLevel level) => Entries.Where(e => e.Level == level);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
