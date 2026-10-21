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
    public async Task<bool> HasProjectAsync(string project, CancellationToken ct)
    {
        // A never-indexed project has no backing table, so ensure the (empty)
        // collection exists first; a filtered scan capped at one row then probes
        // for any chunk belonging to the project.
        await _collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);

        await foreach (var _ in _collection
                           .GetAsync(r => r.Project == project, top: 1, options: null!, ct)
                           .ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }

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
    public async Task ReplaceFileAsync(string project, string relativePath, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
    {
        // Same replace semantics as ReplaceProjectAsync, scoped to one file: gather the
        // current rows for (project, path) via a filtered scan, delete them by key, then
        // upsert the new set. Empty chunks degenerates to a pure delete.
        var staleIds = new List<string>();
        await foreach (var existing in _collection
                           .GetAsync(r => r.Project == project && r.Path == relativePath, MaxProjectChunks, options: null!, ct)
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
        // A project that was never indexed has no backing collection, so the
        // underlying SqliteVec store would throw "no such table" on search.
        // Ensure the (empty) collection exists first so a never-indexed project
        // degrades to zero results instead of an exception. Idempotent: a no-op
        // once the collection has been created by InitializeAsync or a replace.
        await _collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);

        var options = new VectorSearchOptions<ChunkRecord> { Filter = r => r.Project == project, IncludeVectors = false };
        var results = new List<ChunkRecord>();
        await foreach (var hit in _collection.SearchAsync(query, top, options, ct).ConfigureAwait(false))
        {
            results.Add(hit.Record);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChunkRecord>> FindSymbolAsync(string project, string symbol, string? kind, int top, CancellationToken ct)
    {
        // Same graceful-empty guard as SearchAsync: a never-indexed project has no
        // backing table, so ensure the (empty) collection exists before scanning.
        await _collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);

        // Fetch the project's candidate rows via the proven filtered scan, then match
        // in memory: SQLite TEXT == is binary/case-sensitive, so case-insensitivity and
        // contains semantics are applied here in C#.
        var candidates = new List<ChunkRecord>();
        await foreach (var record in _collection
                           .GetAsync(r => r.Project == project, MaxProjectChunks, options: null!, ct)
                           .ConfigureAwait(false))
        {
            candidates.Add(record);
        }

        var hasKind = !string.IsNullOrEmpty(kind);

        bool KindMatches(ChunkRecord r) =>
            !hasKind || string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase);

        var exact = candidates
            .Where(r => !string.IsNullOrEmpty(r.Symbol)
                        && string.Equals(r.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                        && KindMatches(r));

        var partial = candidates
            .Where(r => !string.IsNullOrEmpty(r.Symbol)
                        && !string.Equals(r.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                        && r.Symbol.Contains(symbol, StringComparison.OrdinalIgnoreCase)
                        && KindMatches(r));

        // Exact matches before partial matches, deduped by Id, capped at top.
        // A non-positive top means unbounded: treat it as effectively no cap.
        var cap = top > 0 ? top : int.MaxValue;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ChunkRecord>();
        foreach (var record in exact.Concat(partial))
        {
            if (seen.Add(record.Id))
            {
                ordered.Add(record);
                if (ordered.Count >= cap)
                {
                    break;
                }
            }
        }

        return ordered;
    }
}
