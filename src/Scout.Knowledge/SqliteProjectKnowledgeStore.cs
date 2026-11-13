using System.Text.RegularExpressions;
using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;

namespace Scout.Knowledge;

/// <summary>
/// SQLite backed <see cref="IProjectKnowledgeStore"/>: a sqlite-vec collection for the chunks that carry a vector,
/// and an FTS5 table <c>chunk_fts</c> in the same file for every chunk, which answers existence, symbol lookup and
/// text search with or without embeddings. A replace writes the vectors first and the text rows last, so a failed
/// vector write never leaves a project that looks indexed; it leaves the vectors the project had, and a replace removes every vector it no longer holds. A project found only in the vector collection and in no row
/// of <c>project_source</c> (a database written before the text table existed) has its text rows filled from the stored chunks on the first existence check,
/// with no embedding call. <c>project_source</c> keeps the path a project was indexed from; its row is written, with no path yet, before a project's vectors, so vectors alone beside a row are a write that did not finish. WAL mode: a read never waits for a write.
/// </summary>
public sealed partial class SqliteProjectKnowledgeStore : IProjectKnowledgeStore
{
    private const string CollectionName = "project_chunks";

    private const int MaxProjectChunks = 100_000;

    private const int MaxSearchTerms = 12;

    private const string TextSchema = """
        PRAGMA journal_mode = WAL;
        CREATE VIRTUAL TABLE IF NOT EXISTS chunk_fts USING fts5(
            id UNINDEXED,
            project UNINDEXED,
            path,
            kind UNINDEXED,
            symbol,
            start_line UNINDEXED,
            end_line UNINDEXED,
            content,
            tokenize = 'unicode61');
        CREATE TABLE IF NOT EXISTS project_source (project TEXT PRIMARY KEY, repo_path TEXT NOT NULL);
        """;

    private const string TextColumns = "id, project, path, kind, symbol, start_line, end_line, content";

    private readonly string _connectionString;

    private readonly VectorStoreCollection<string, ChunkRecord> _collection;

    private readonly int _vectorPage;

    /// <summary>Opens the SQLite file and loads the sqlite-vec extension.</summary>
    /// <param name="connectionString">SQLite connection string, e.g. "Data Source=knowledge.db".</param>
    public SqliteProjectKnowledgeStore(string connectionString)
        : this(connectionString, MaxProjectChunks)
    {
    }

    internal SqliteProjectKnowledgeStore(string connectionString, int vectorPage)
    {
        _connectionString = connectionString;
        _vectorPage = vectorPage;
        var store = new SqliteVectorStore(connectionString);
        _collection = store.GetCollection<string, ChunkRecord>(CollectionName);
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasProjectAsync(string project, CancellationToken ct)
    {
        await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
        {
            await using var command = Command(connection, "SELECT 1 FROM chunk_fts WHERE project = $project LIMIT 1;");
            command.Parameters.AddWithValue("$project", project);
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            {
                return true;
            }
        }

        if (await GetIndexedPathAsync(project, ct).ConfigureAwait(false) is not null)
        {
            return false;
        }

        await _collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
        var vectorOnly = new List<ChunkRecord>();
        await foreach (var record in _collection
                           .GetAsync(r => r.Project == project, MaxProjectChunks, options: null!, ct)
                           .ConfigureAwait(false))
        {
            vectorOnly.Add(record);
        }

        if (vectorOnly.Count == 0)
        {
            return false;
        }

        await ReplaceTextRowsAsync(project, relativePath: null, vectorOnly, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<string?> GetIndexedPathAsync(string project, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, "SELECT repo_path FROM project_source WHERE project = $project;");
        command.Parameters.AddWithValue("$project", project);
        return (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetIndexedPathAsync(string project, string repoPath, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(
            connection,
            """
            INSERT INTO project_source (project, repo_path) VALUES ($project, $path)
            ON CONFLICT (project) DO UPDATE SET repo_path = excluded.repo_path;
            """);
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$path", repoPath);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReplaceProjectAsync(string project, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
    {
        await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
        await using (var started = Command(
            connection,
            "INSERT INTO project_source (project, repo_path) VALUES ($project, '') ON CONFLICT (project) DO NOTHING;"))
        {
            started.Parameters.AddWithValue("$project", project);
            await started.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await ReplaceVectorsAsync(r => r.Project == project, chunks, ct).ConfigureAwait(false);
        await ReplaceTextRowsAsync(project, relativePath: null, chunks, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReplaceFileAsync(string project, string relativePath, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
    {
        await ReplaceVectorsAsync(r => r.Project == project && r.Path == relativePath, chunks, ct).ConfigureAwait(false);
        await ReplaceTextRowsAsync(project, relativePath, chunks, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChunkRecord>> SearchAsync(string project, ReadOnlyMemory<float> query, int top, CancellationToken ct)
    {
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
    public async Task<IReadOnlyList<ChunkRecord>> SearchTextAsync(string project, string text, int top, CancellationToken ct)
    {
        var terms = Words().Matches(text ?? string.Empty)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSearchTerms)
            .Select(Quote)
            .ToList();
        if (terms.Count == 0)
        {
            return Array.Empty<ChunkRecord>();
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(
            connection,
            $"SELECT {TextColumns} FROM chunk_fts WHERE chunk_fts MATCH $match AND project = $project ORDER BY bm25(chunk_fts) LIMIT $top;");
        command.Parameters.AddWithValue("$match", string.Join(" OR ", terms));
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$top", top);
        return await ReadAsync(command, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChunkRecord>> FindSymbolAsync(string project, string symbol, string? kind, int top, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(
            connection,
            $"""
            SELECT {TextColumns} FROM chunk_fts
            WHERE project = $project AND symbol <> '' AND symbol LIKE $pattern ESCAPE '\'
              AND ($kind IS NULL OR kind = $kind COLLATE NOCASE)
            ORDER BY (symbol = $symbol COLLATE NOCASE) DESC, rowid
            LIMIT $top;
            """);
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$pattern", "%" + EscapeLike(symbol) + "%");
        command.Parameters.AddWithValue("$kind", string.IsNullOrEmpty(kind) ? DBNull.Value : kind);
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$top", top > 0 ? top : -1);
        return await ReadAsync(command, ct).ConfigureAwait(false);
    }

    private async Task ReplaceTextRowsAsync(string project, string? relativePath, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var delete = Command(
            connection,
            relativePath is null
                ? "DELETE FROM chunk_fts WHERE project = $project;"
                : "DELETE FROM chunk_fts WHERE project = $project AND path = $path;"))
        {
            delete.Parameters.AddWithValue("$project", project);
            delete.Parameters.AddWithValue("$path", (object?)relativePath ?? DBNull.Value);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var insert = Command(
            connection,
            $"INSERT INTO chunk_fts ({TextColumns}) VALUES ($id, $project, $path, $kind, $symbol, $start, $end, $content);"))
        {
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var owner = insert.Parameters.Add("$project", SqliteType.Text);
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var kind = insert.Parameters.Add("$kind", SqliteType.Text);
            var symbol = insert.Parameters.Add("$symbol", SqliteType.Text);
            var start = insert.Parameters.Add("$start", SqliteType.Integer);
            var end = insert.Parameters.Add("$end", SqliteType.Integer);
            var content = insert.Parameters.Add("$content", SqliteType.Text);
            foreach (var chunk in chunks)
            {
                id.Value = chunk.Id;
                owner.Value = chunk.Project;
                path.Value = chunk.Path;
                kind.Value = chunk.Kind;
                symbol.Value = chunk.Symbol ?? string.Empty;
                start.Value = chunk.StartLine;
                end.Value = chunk.EndLine;
                content.Value = chunk.Content;
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task ReplaceVectorsAsync(
        System.Linq.Expressions.Expression<Func<ChunkRecord, bool>> scope,
        IReadOnlyList<ChunkRecord> chunks,
        CancellationToken ct)
    {
        await _collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);

        var previous = new HashSet<string>(StringComparer.Ordinal);
        var page = new FilteredRecordRetrievalOptions<ChunkRecord> { OrderBy = order => order.Ascending(r => r.Id) };
        int read;
        do
        {
            read = 0;
            await foreach (var existing in _collection.GetAsync(scope, _vectorPage, page, ct).ConfigureAwait(false))
            {
                previous.Add(existing.Id);
                read++;
            }

            page.Skip += read;
        }
        while (read == _vectorPage);

        var withVectors = chunks.Where(c => c.Embedding.Length > 0).ToList();
        if (withVectors.Count > 0)
        {
            try
            {
                await _collection.UpsertAsync(withVectors, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                var added = withVectors.Select(c => c.Id).Where(id => !previous.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
                if (added.Count > 0)
                {
                    await _collection.DeleteAsync(added, CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        }

        var stale = previous.Except(withVectors.Select(c => c.Id), StringComparer.Ordinal).ToList();
        if (stale.Count > 0)
        {
            await _collection.DeleteAsync(stale, ct).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var schema = Command(connection, TextSchema);
            await schema.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static async Task<List<ChunkRecord>> ReadAsync(SqliteCommand command, CancellationToken ct)
    {
        var records = new List<ChunkRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            records.Add(new ChunkRecord
            {
                Id = reader.GetString(0),
                Project = reader.GetString(1),
                Path = reader.GetString(2),
                Kind = reader.GetString(3),
                Symbol = reader.GetString(4),
                StartLine = reader.GetInt32(5),
                EndLine = reader.GetInt32(6),
                Content = reader.GetString(7),
            });
        }

        return records;
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string Quote(string term) => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"[\p{L}\p{N}]{3,}")]
    private static partial Regex Words();
}
