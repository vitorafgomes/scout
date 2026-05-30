namespace Scout.Knowledge;

/// <summary>Persistence + similarity search over indexed project chunks.</summary>
public interface IProjectKnowledgeStore
{
    /// <summary>Ensure the backing collection exists.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>Replace all chunks for a project with the supplied set.</summary>
    /// <param name="project">Project name whose chunks are being replaced.</param>
    /// <param name="chunks">The complete new chunk set for the project.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReplaceProjectAsync(string project, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct);

    /// <summary>Replace the chunks for a single file within a project; empty <paramref name="chunks"/> deletes the file's rows.</summary>
    /// <param name="project">Project name the file belongs to.</param>
    /// <param name="relativePath">Repo-relative file path whose chunks are being replaced.</param>
    /// <param name="chunks">The complete new chunk set for the file; empty to delete only.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReplaceFileAsync(string project, string relativePath, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct);

    /// <summary>Top-K cosine search restricted to one project.</summary>
    /// <param name="project">Project name to restrict the search to.</param>
    /// <param name="query">Query embedding vector.</param>
    /// <param name="top">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching chunks ordered nearest first.</returns>
    Task<IReadOnlyList<ChunkRecord>> SearchAsync(string project, ReadOnlyMemory<float> query, int top, CancellationToken ct);

    /// <summary>
    /// Metadata-only symbol lookup restricted to one project. No embedding is used.
    /// Exact (case-insensitive) symbol matches come first, then partial (contains) matches.
    /// </summary>
    /// <param name="project">Project name to restrict the lookup to.</param>
    /// <param name="symbol">Symbol name to match against <see cref="ChunkRecord.Symbol"/>.</param>
    /// <param name="kind">Optional coarse kind filter ("code"/"doc"); null or empty to skip.</param>
    /// <param name="top">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching chunks, exact matches before partial matches, capped at <paramref name="top"/>.</returns>
    Task<IReadOnlyList<ChunkRecord>> FindSymbolAsync(string project, string symbol, string? kind, int top, CancellationToken ct);
}
