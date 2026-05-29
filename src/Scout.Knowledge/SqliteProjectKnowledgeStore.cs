using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace Scout.Knowledge;

/// <summary>SQLite-vec backed <see cref="IProjectKnowledgeStore"/>. Local file, no service.</summary>
public sealed class SqliteProjectKnowledgeStore : IProjectKnowledgeStore
{
    private const string CollectionName = "project_chunks";

    // Upper bound for the id-gathering scan during replace. Indexed projects are
    // expected to stay well below this; large enough to be effectively unbounded here.
    private const int MaxProjectChunks = 100_000;

    private readonly VectorStoreCollection<string, ChunkRecord> _collection;

    /// <summary>Opens the SQLite file and loads the sqlite-vec extension.</summary>
    /// <param name="connectionString">SQLite connection string, e.g. "Data Source=knowledge.db".</param>
    public SqliteProjectKnowledgeStore(string connectionString)
    {
        var store = new SqliteVectorStore(connectionString);
        _collection = store.GetCollection<string, ChunkRecord>(CollectionName);
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct) => _collection.EnsureCollectionExistsAsync(ct);

    /// <inheritdoc />
    public async Task ReplaceProjectAsync(string project, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
    {
        // Replace semantics: drop every existing row for this project, then upsert the new set.
        // Gather the project's current ids via a filtered scan and delete them by key so that
        // chunks for code that no longer exists are removed (deterministic ids alone would only
        // overwrite survivors, never delete stale ones).
        var staleIds = new List<string>();
        await foreach (var existing in _collection
                           .GetAsync(r => r.Project == project, MaxProjectChunks, options: null!, ct)
                           .ConfigureAwait(false))
        {
            staleIds.Add(existing.Id);
        }

        if (staleIds.Count > 0)
        {
            await _collection.DeleteAsync(staleIds, ct).ConfigureAwait(false);
        }

        if (chunks.Count > 0)
        {
            await _collection.UpsertAsync(chunks, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChunkRecord>> SearchAsync(string project, ReadOnlyMemory<float> query, int top, CancellationToken ct)
    {
        var options = new VectorSearchOptions<ChunkRecord> { Filter = r => r.Project == project, IncludeVectors = false };
        var results = new List<ChunkRecord>();
        await foreach (var hit in _collection.SearchAsync(query, top, options, ct).ConfigureAwait(false))
        {
            results.Add(hit.Record);
        }

        return results;
    }
}
