using System.Collections.Concurrent;

namespace Scout.Host.Worker.Providers;

/// <summary>
/// Thread-safe in-memory <see cref="ILocalRuntimeRegistry"/>. Registered as
/// a singleton so every consumer sees the same ownership view for the
/// lifetime of the worker process.
/// </summary>
public sealed class InMemoryLocalRuntimeRegistry : ILocalRuntimeRegistry
{
    private readonly ConcurrentDictionary<string, ILaunchedProcess> _entries =
        new(StringComparer.Ordinal);

    public void Track(string runtimeId, ILaunchedProcess process)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);
        ArgumentNullException.ThrowIfNull(process);

        _entries[runtimeId] = process;
    }

    public ILaunchedProcess? GetOwned(string runtimeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);

        return _entries.TryGetValue(runtimeId, out var process) ? process : null;
    }

    public void Untrack(string runtimeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);

        _entries.TryRemove(runtimeId, out _);
    }
}
