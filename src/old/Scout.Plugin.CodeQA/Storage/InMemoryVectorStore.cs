using Scout.Plugin.CodeQA.Domain;

namespace Scout.Plugin.CodeQA.Storage;

/// <summary>
/// In-process vector store. Normalizes vectors on insert so search collapses
/// to a dot product. Thread-safe via <see cref="ReaderWriterLockSlim"/> —
/// retrieval is the hot path (shared lock), ingestion is infrequent
/// (exclusive lock). Good enough for V1 repos that fit in memory; persistent
/// backends (pgvector, Qdrant) land as follow-up issues.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore, IDisposable
{
    private readonly Dictionary<string, StoredChunk> _chunks = new(StringComparer.Ordinal);
    private readonly ReaderWriterLockSlim _lock = new();

    public Task UpsertAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();

        _lock.EnterWriteLock();
        try
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalized = Normalize(chunk.Embedding.Span);
                _chunks[chunk.Id] = new StoredChunk(chunk, normalized);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        CancellationToken cancellationToken)
    {
        if (topK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topK), "topK must be positive.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedQuery = Normalize(queryEmbedding.Span);

        _lock.EnterReadLock();
        try
        {
            if (_chunks.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<VectorSearchResult>>(
                    Array.Empty<VectorSearchResult>());
            }

            var scored = new List<VectorSearchResult>(_chunks.Count);
            foreach (var stored in _chunks.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var score = Dot(normalizedQuery, stored.Normalized);
                scored.Add(new VectorSearchResult(stored.Chunk, score));
            }

            scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            var take = Math.Min(topK, scored.Count);
            var result = new VectorSearchResult[take];
            scored.CopyTo(0, result, 0, take);
            return Task.FromResult<IReadOnlyList<VectorSearchResult>>(result);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public Task<int> CountAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lock.EnterReadLock();
        try
        {
            return Task.FromResult(_chunks.Count);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lock.EnterWriteLock();
        try
        {
            _chunks.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _lock.Dispose();

    private static float[] Normalize(ReadOnlySpan<float> vector)
    {
        if (vector.Length == 0)
        {
            throw new ArgumentException("Embedding vector must not be empty.", nameof(vector));
        }

        double sumSquares = 0d;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSquares += vector[i] * (double)vector[i];
        }

        var magnitude = Math.Sqrt(sumSquares);
        var result = new float[vector.Length];
        if (magnitude == 0d)
        {
            // Zero vector — no direction. Copy as-is; dot products will be 0.
            vector.CopyTo(result);
            return result;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = (float)(vector[i] / magnitude);
        }
        return result;
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new InvalidOperationException(
                "Query and stored embedding have different dimensions. " +
                $"Query = {a.Length}, stored = {b.Length}. " +
                "Re-index with the current embedder model.");
        }
        double sum = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * (double)b[i];
        }
        return sum;
    }

    private readonly record struct StoredChunk(CodeChunk Chunk, float[] Normalized);
}
