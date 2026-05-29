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

    /// <summary>Top-K cosine search restricted to one project.</summary>
    /// <param name="project">Project name to restrict the search to.</param>
    /// <param name="query">Query embedding vector.</param>
    /// <param name="top">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching chunks ordered nearest first.</returns>
    Task<IReadOnlyList<ChunkRecord>> SearchAsync(string project, ReadOnlyMemory<float> query, int top, CancellationToken ct);
}
