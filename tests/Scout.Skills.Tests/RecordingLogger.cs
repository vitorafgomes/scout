using Microsoft.Extensions.Logging;

namespace Scout.Skills.Tests;

/// <summary>One entry a <see cref="RecordingLogger{T}"/> kept: the level, the formatted message and the exception.</summary>
internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>Logger that keeps every formatted entry so a test can inspect what was written.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
}
