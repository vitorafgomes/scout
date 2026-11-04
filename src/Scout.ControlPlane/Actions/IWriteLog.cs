namespace Scout.ControlPlane.Actions;

/// <summary>Append-only record of every write a feature performs.</summary>
public interface IWriteLog
{
    /// <summary>Append one entry.</summary>
    Task AppendAsync(WriteLogEntry entry, CancellationToken ct);

    /// <summary>The last <paramref name="limit"/> entries, newest first.</summary>
    Task<IReadOnlyList<WriteLogEntry>> ListAsync(int limit, CancellationToken ct);

    /// <summary>The entries from the end back to the first one older than <paramref name="since"/>, newest first.</summary>
    Task<IReadOnlyList<WriteLogEntry>> ListSinceAsync(DateTimeOffset since, CancellationToken ct);
}
