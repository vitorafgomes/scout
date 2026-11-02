namespace Scout.ControlPlane.Actions;

/// <summary>Append-only record of every write a feature performs.</summary>
public interface IWriteLog
{
    /// <summary>Append one entry.</summary>
    Task AppendAsync(WriteLogEntry entry, CancellationToken ct);

    /// <summary>The last <paramref name="limit"/> entries, newest first.</summary>
    Task<IReadOnlyList<WriteLogEntry>> ListAsync(int limit, CancellationToken ct);
}
