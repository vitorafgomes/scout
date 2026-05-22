namespace Scout.Host.Worker.Providers;

/// <summary>
/// Tracks the child processes the worker has spawned, keyed by runtime id
/// (e.g. <c>"ollama"</c>). The registry is in-memory only: a worker restart
/// wipes ownership, and any orphan daemon is reconciled by the autostart
/// service via a fresh ping on the next boot.
/// </summary>
public interface ILocalRuntimeRegistry
{
    /// <summary>Records that the worker owns <paramref name="process"/>.</summary>
    void Track(string runtimeId, ILaunchedProcess process);

    /// <summary>
    /// Returns the owned process for <paramref name="runtimeId"/> or null
    /// when nothing is tracked.
    /// </summary>
    ILaunchedProcess? GetOwned(string runtimeId);

    /// <summary>Drops any entry for <paramref name="runtimeId"/>.</summary>
    void Untrack(string runtimeId);
}
