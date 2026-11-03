using Microsoft.Extensions.Logging;

namespace Scout.Operator.Tests.Fakes;

/// <summary>A logger that keeps every entry's level and rendered message.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock _lock = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
