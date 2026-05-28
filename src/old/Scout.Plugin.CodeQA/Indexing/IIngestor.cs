namespace Scout.Plugin.CodeQA.Indexing;

/// <summary>
/// Fetches the source files that feed the CodeQA index. One call returns
/// the full snapshot; incremental ingestion is a V2 concern.
/// </summary>
/// <remarks>
/// Implementations are the only boundary the CodeQA plugin has with a
/// source host (GitHub today, others later). Swapping hosts means swapping
/// the registration, never the callers.
/// </remarks>
public interface IIngestor
{
    /// <summary>
    /// Pulls the current snapshot of ingestable files.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// Files filtered by include/exclude globs and the configured size cap,
    /// in deterministic ordinal <c>Path</c> order. Binary blobs are
    /// excluded. The returned list is a snapshot and is safe to enumerate
    /// after the call completes.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Configuration is missing (PAT, owner, name, branch) or the source
    /// host reported a truncated tree, rate-limit exhaustion, authentication
    /// failure, or a missing repository/branch. V1 does not retry.
    /// </exception>
    Task<IReadOnlyList<RawFile>> IngestAsync(CancellationToken cancellationToken);
}
