using Microsoft.Extensions.Logging;
using Entry = (Microsoft.Extensions.Logging.LogLevel Level, string Message);

namespace Scout.Operator.Tests.Fakes;

/// <summary>
/// An in-memory log for tests. Each call is kept in order as its level and its rendered message. Reading is safe
/// while the code under test still writes from another thread.
/// </summary>
internal class RecordingLogger : ILogger
{
    private readonly List<Entry> _kept = [];

    /// <summary>A copy of everything kept so far, oldest first.</summary>
    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_kept)
            {
                return _kept.ToArray();
            }
        }
    }

    IDisposable? ILogger.BeginScope<TScope>(TScope scope) => null;

    bool ILogger.IsEnabled(LogLevel level) => true;

    void ILogger.Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> describe)
    {
        var printed = describe(state, error);

        lock (_kept)
        {
            _kept.Add((level, printed));
        }
    }
}

/// <summary>The same in-memory log, handed to a constructor that asks for a logger of its own category.</summary>
internal sealed class RecordingLogger<TCategory> : RecordingLogger, ILogger<TCategory>;
