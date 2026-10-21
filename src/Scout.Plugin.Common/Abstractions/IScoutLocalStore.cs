namespace Scout.Plugin.Common.Abstractions;

/// <summary>
/// Per-collection JSON persistence for Scout's own local state (reminders,
/// lists, notes, ledger, ...). This is the only write surface plugins are
/// allowed to touch besides messages in the originating chat channel: it
/// stores Scout-owned data on the local machine, never anything external.
/// </summary>
public interface IScoutLocalStore
{
    /// <summary>
    /// Load the document for <paramref name="collection"/>, returning a fresh
    /// <typeparamref name="T"/> when the collection does not exist yet.
    /// </summary>
    Task<T> ReadOrCreateAsync<T>(string collection, CancellationToken cancellationToken = default)
        where T : new();

    /// <summary>
    /// Atomically read-modify-write the document for <paramref name="collection"/>.
    /// The mutation runs under a per-collection lock and the file is replaced
    /// atomically, so concurrent callers never observe a partial write. Returns
    /// the mutated document.
    /// </summary>
    Task<T> MutateAsync<T>(
        string collection,
        Action<T> mutate,
        CancellationToken cancellationToken = default)
        where T : new();
}
