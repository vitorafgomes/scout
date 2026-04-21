using Scout.Plugin.CodeQA.Domain;

namespace Scout.Plugin.CodeQA.Storage;

/// <summary>
/// Persistence contract for indexed <see cref="CodeChunk"/> values.
/// Implementations must accept cancellation and be safe to call from
/// multiple threads concurrently — ingestion writes and chat queries may
/// overlap in time.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Insert or replace a batch of chunks. Matching is by
    /// <see cref="CodeChunk.Id"/>.
    /// </summary>
    Task UpsertAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken);

    /// <summary>
    /// Return the top <paramref name="topK"/> chunks ranked by cosine
    /// similarity against <paramref name="queryEmbedding"/>, in descending
    /// order. Each result carries the similarity score in [-1, 1].
    /// </summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        CancellationToken cancellationToken);

    /// <summary>Total number of chunks currently stored.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>Remove every chunk.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One hit from <see cref="IVectorStore.SearchAsync"/>. The chunk preserves
/// its file / line metadata so the composer can turn it into a <see cref="Citation"/>.
/// </summary>
public sealed record VectorSearchResult(CodeChunk Chunk, double Score);
