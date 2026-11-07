using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace Scout.Knowledge;

/// <summary>
/// SQLite backed <see cref="IProjectKnowledgeStore"/>: a sqlite-vec collection for the chunks that carry a vector,
/// and an FTS5 table <c>chunk_fts</c> in the same file for every chunk, which answers existence, symbol lookup and
/// text search with or without embeddings. A replace writes the vectors first and the text rows last, so a failed
/// vector write never leaves a project that looks indexed. Local file, no service.
/// </summary>
public sealed partial class SqliteProjectKnowledgeStore : IProjectKnowledgeStore
{
    private const string CollectionName = "project_chunks";

    private const int MaxProjectChunks = 100_000;

    private const int MaxSearchTerms = 12;

    private const string TextSchema = """
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
        """;

    private const string TextColumns = "id, project, path, kind, symbol, start_line, end_line, content";

    private readonly string _connectionString;

    private readonly VectorStoreCollection<string, ChunkRecord> _collection;

    /// <summary>Opens the SQLite file and loads the sqlite-vec extension.</summary>
    /// <param name="connectionString">SQLite connection string, e.g. "Data Source=knowledge.db".</param>
    public SqliteProjectKnowledgeStore(string connectionString)
    {
        _connectionString = connectionString;
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
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, "SELECT 1 FROM chunk_fts WHERE project = $project LIMIT 1;");
        command.Parameters.AddWithValue("$project", project);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public async Task ReplaceProjectAsync(string project, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
    {
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
        List<ChunkRecord> candidates;
        await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
        {
            await using var command = Command(
                connection,
                $"SELECT {TextColumns} FROM chunk_fts WHERE project = $project AND symbol <> '' AND symbol LIKE $pattern ESCAPE '\\';");
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$pattern", "%" + EscapeLike(symbol) + "%");
            candidates = await ReadAsync(command, ct).ConfigureAwait(false);
        }

        return candidates
            .Where(r => string.IsNullOrEmpty(kind) || string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => string.Equals(r.Symbol, symbol, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .DistinctBy(r => r.Id, StringComparer.Ordinal)
            .Take(top > 0 ? top : int.MaxValue)
            .ToList();
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

        var staleIds = new List<string>();
        await foreach (var existing in _collection
                           .GetAsync(scope, MaxProjectChunks, options: null!, ct)
                           .ConfigureAwait(false))
        {
            staleIds.Add(existing.Id);
        }

        if (staleIds.Count > 0)
        {
            await _collection.DeleteAsync(staleIds, ct).ConfigureAwait(false);
        }

        var withVectors = chunks.Where(c => c.Embedding.Length > 0).ToList();
        if (withVectors.Count > 0)
        {
            await _collection.UpsertAsync(withVectors, ct).ConfigureAwait(false);
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

    [GeneratedRegex(@"[\p{L}\p{N}]{2,}")]
    private static partial Regex Words();
}
