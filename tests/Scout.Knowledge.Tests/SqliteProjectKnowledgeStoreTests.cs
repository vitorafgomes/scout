using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;

namespace Scout.Knowledge.Tests;

public sealed class SqliteProjectKnowledgeStoreTests : IDisposable
{
    private static readonly float[] WrongSizeVector = [1f, 2f, 3f];

    private const int Dimensions = 768;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"scout-knowledge-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    private static ReadOnlyMemory<float> Vector(params (int Index, float Value)[] components)
    {
        var data = new float[Dimensions];
        foreach (var (index, value) in components)
        {
            data[index] = value;
        }

        return data;
    }

    private static ChunkRecord Chunk(string project, string id, ReadOnlyMemory<float> embedding) => new()
    {
        Id = $"{project}:{id}",
        Project = project,
        Path = $"{id}.cs",
        Kind = "code",
        Symbol = id,
        StartLine = 1,
        EndLine = 10,
        Content = $"content for {id}",
        Embedding = embedding,
    };

    private static ChunkRecord FileChunk(string project, string path, string id, ReadOnlyMemory<float> embedding) => new()
    {
        Id = $"{project}:{path}:{id}",
        Project = project,
        Path = path,
        Kind = "code",
        Symbol = id,
        StartLine = 1,
        EndLine = 10,
        Content = $"content for {id}",
        Embedding = embedding,
    };

    private static ChunkRecord SymbolChunk(string project, string id, string symbol, string kind) => new()
    {
        Id = $"{project}:{id}",
        Project = project,
        Path = $"{id}.cs",
        Kind = kind,
        Symbol = symbol,
        StartLine = 1,
        EndLine = 10,
        Content = $"content for {symbol}",
        Embedding = Vector((0, 1f)),
    };

    [Fact]
    public async Task FindSymbolAsync_ReturnsExactMatch_CaseInsensitively()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                SymbolChunk("alpha", "a", "SearchAsync", "code"),
                SymbolChunk("alpha", "b", "Unrelated", "code"),
            },
            CancellationToken.None);

        var results = await store.FindSymbolAsync("alpha", "searchasync", kind: null, top: 10, CancellationToken.None);

        results.ShouldHaveSingleItem().Symbol.ShouldBe("SearchAsync");
    }

    [Fact]
    public async Task FindSymbolAsync_PutsExactMatchesBeforePartialMatches()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                SymbolChunk("alpha", "partial", "FindSymbolAsync", "code"),
                SymbolChunk("alpha", "exact", "Find", "code"),
            },
            CancellationToken.None);

        var results = await store.FindSymbolAsync("alpha", "Find", kind: null, top: 10, CancellationToken.None);

        results.Count.ShouldBe(2);
        results[0].Symbol.ShouldBe("Find");
        results[1].Symbol.ShouldBe("FindSymbolAsync");
    }

    [Fact]
    public async Task FindSymbolAsync_FiltersByKind_CaseInsensitively()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                SymbolChunk("alpha", "a", "Overview", "code"),
                SymbolChunk("alpha", "b", "Overview", "doc"),
            },
            CancellationToken.None);

        var results = await store.FindSymbolAsync("alpha", "Overview", kind: "DOC", top: 10, CancellationToken.None);

        results.ShouldHaveSingleItem().Kind.ShouldBe("doc");
    }

    [Fact]
    public async Task FindSymbolAsync_RespectsProjectIsolation()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[] { SymbolChunk("alpha", "a", "Shared", "code") },
            CancellationToken.None);
        await store.ReplaceProjectAsync(
            "beta",
            new[] { SymbolChunk("beta", "b", "Shared", "code") },
            CancellationToken.None);

        var results = await store.FindSymbolAsync("alpha", "Shared", kind: null, top: 10, CancellationToken.None);

        results.ShouldHaveSingleItem().Project.ShouldBe("alpha");
    }

    [Fact]
    public async Task FindSymbolAsync_CapsResultsAtTop()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                SymbolChunk("alpha", "a", "Handler", "code"),
                SymbolChunk("alpha", "b", "Handler", "code"),
                SymbolChunk("alpha", "c", "Handler", "code"),
            },
            CancellationToken.None);

        var results = await store.FindSymbolAsync("alpha", "Handler", kind: null, top: 2, CancellationToken.None);

        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task FindSymbolAsync_WithNonPositiveTop_ReturnsAllMatches()
    {
        // A non-positive top means unbounded: every match must be returned.
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                SymbolChunk("alpha", "a", "Handler", "code"),
                SymbolChunk("alpha", "b", "Handler", "code"),
                SymbolChunk("alpha", "c", "Handler", "code"),
            },
            CancellationToken.None);

        var zero = await store.FindSymbolAsync("alpha", "Handler", kind: null, top: 0, CancellationToken.None);
        zero.Count.ShouldBe(3);

        var negative = await store.FindSymbolAsync("alpha", "Handler", kind: null, top: -1, CancellationToken.None);
        negative.Count.ShouldBe(3);
    }

    [Fact]
    public async Task FindSymbolAsync_WithKindMatchingNothing_ReturnsEmpty()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                SymbolChunk("alpha", "a", "Overview", "code"),
                SymbolChunk("alpha", "b", "Overview", "doc"),
            },
            CancellationToken.None);

        // No row has this kind: the filter must exclude everything.
        var unmatched = await store.FindSymbolAsync("alpha", "Overview", kind: "schema", top: 10, CancellationToken.None);
        unmatched.ShouldBeEmpty();

        // Empty kind means "no filter": both rows match.
        var noFilter = await store.FindSymbolAsync("alpha", "Overview", kind: string.Empty, top: 10, CancellationToken.None);
        noFilter.Count.ShouldBe(2);
    }

    [Fact]
    public async Task FindSymbolAsync_OnUninitializedStore_ReturnsEmptyWithoutThrowing()
    {
        // Fresh temp db: collection was never created. Lookup must degrade to empty,
        // not throw "no such table".
        var store = new SqliteProjectKnowledgeStore(ConnectionString);

        IReadOnlyList<ChunkRecord> results = Array.Empty<ChunkRecord>();
        await Should.NotThrowAsync(async () =>
            results = await store.FindSymbolAsync("never-indexed", "Anything", kind: null, top: 5, CancellationToken.None));

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ReturnsOnlyRequestedProject_NearestFirst()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        // alpha: two chunks, one aligned with the query (index 0), one orthogonal (index 5).
        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                Chunk("alpha", "near", Vector((0, 1f))),
                Chunk("alpha", "far", Vector((5, 1f))),
            },
            CancellationToken.None);

        // beta: also aligned with the query, must be excluded by the project filter.
        await store.ReplaceProjectAsync(
            "beta",
            new[] { Chunk("beta", "intruder", Vector((0, 1f))) },
            CancellationToken.None);

        var query = Vector((0, 1f));
        var results = await store.SearchAsync("alpha", query, top: 10, CancellationToken.None);

        results.ShouldAllBe(r => r.Project == "alpha");
        results.Count.ShouldBe(2);
        results[0].Id.ShouldBe("alpha:near");
    }

    [Fact]
    public async Task ReplaceProjectAsync_CalledTwice_LeavesOnlyLatestSet()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                Chunk("alpha", "old-a", Vector((0, 1f))),
                Chunk("alpha", "old-b", Vector((1, 1f))),
            },
            CancellationToken.None);

        // Second call has a different id set; replace semantics must drop the old rows.
        await store.ReplaceProjectAsync(
            "alpha",
            new[] { Chunk("alpha", "new-only", Vector((0, 1f))) },
            CancellationToken.None);

        var results = await store.SearchAsync("alpha", Vector((0, 1f)), top: 10, CancellationToken.None);

        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe("alpha:new-only");
    }

    [Fact]
    public async Task SearchAsync_OnEmptyStore_ReturnsEmpty()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        var results = await store.SearchAsync("missing", Vector((0, 1f)), top: 5, CancellationToken.None);

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_on_uninitialized_store_returns_empty_without_throwing()
    {
        // Fresh temp db: collection was never created (no InitializeAsync, no Replace).
        // Search must degrade to an empty result, not throw "no such table".
        var store = new SqliteProjectKnowledgeStore(ConnectionString);

        IReadOnlyList<ChunkRecord> results = Array.Empty<ChunkRecord>();
        await Should.NotThrowAsync(async () =>
            results = await store.SearchAsync("never-indexed", Vector((0, 1f)), top: 5, CancellationToken.None));

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplaceFileAsync_ReplacesOnlyThatPath_WithinProject()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                FileChunk("alpha", "Foo.cs", "old", Vector((0, 1f))),
                FileChunk("alpha", "Bar.cs", "keep", Vector((1, 1f))),
            },
            CancellationToken.None);

        // Replace only Foo.cs; Bar.cs rows must survive untouched.
        await store.ReplaceFileAsync(
            "alpha",
            "Foo.cs",
            new[] { FileChunk("alpha", "Foo.cs", "new", Vector((0, 1f))) },
            CancellationToken.None);

        var results = await store.SearchAsync("alpha", Vector((0, 1f)), top: 10, CancellationToken.None);

        results.ShouldContain(r => r.Id == "alpha:Foo.cs:new");
        results.ShouldContain(r => r.Id == "alpha:Bar.cs:keep");
        results.ShouldNotContain(r => r.Id == "alpha:Foo.cs:old");
    }

    [Fact]
    public async Task ReplaceFileAsync_WithEmptyChunks_DeletesTheFilesRows()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                FileChunk("alpha", "Foo.cs", "a", Vector((0, 1f))),
                FileChunk("alpha", "Bar.cs", "keep", Vector((1, 1f))),
            },
            CancellationToken.None);

        await store.ReplaceFileAsync("alpha", "Foo.cs", Array.Empty<ChunkRecord>(), CancellationToken.None);

        var results = await store.SearchAsync("alpha", Vector((0, 1f)), top: 10, CancellationToken.None);

        results.ShouldNotContain(r => r.Path == "Foo.cs");
        results.ShouldContain(r => r.Id == "alpha:Bar.cs:keep");
    }

    [Fact]
    public async Task ReplaceFileAsync_DoesNotTouchOtherProjects()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[] { FileChunk("alpha", "Foo.cs", "a", Vector((0, 1f))) },
            CancellationToken.None);
        await store.ReplaceProjectAsync(
            "beta",
            new[] { FileChunk("beta", "Foo.cs", "b", Vector((0, 1f))) },
            CancellationToken.None);

        // Same relative path, different project: beta must be untouched.
        await store.ReplaceFileAsync("alpha", "Foo.cs", Array.Empty<ChunkRecord>(), CancellationToken.None);

        var beta = await store.SearchAsync("beta", Vector((0, 1f)), top: 10, CancellationToken.None);
        beta.ShouldContain(r => r.Id == "beta:Foo.cs:b");

        var alpha = await store.SearchAsync("alpha", Vector((0, 1f)), top: 10, CancellationToken.None);
        alpha.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplaceFileAsync_WithNoExistingRows_UpsertsCleanly()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceFileAsync(
            "alpha",
            "Foo.cs",
            new[] { FileChunk("alpha", "Foo.cs", "new", Vector((0, 1f))) },
            CancellationToken.None);

        var results = await store.SearchAsync("alpha", Vector((0, 1f)), top: 10, CancellationToken.None);

        results.ShouldHaveSingleItem().Id.ShouldBe("alpha:Foo.cs:new");
    }

    [Fact]
    public async Task HasProjectAsync_ReturnsFalse_ForNeverIndexedProject()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        var has = await store.HasProjectAsync("never-indexed", CancellationToken.None);

        has.ShouldBeFalse();
    }

    [Fact]
    public async Task HasProjectAsync_ReturnsTrue_AfterReplaceProject()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[] { Chunk("alpha", "only", Vector((0, 1f))) },
            CancellationToken.None);

        var has = await store.HasProjectAsync("alpha", CancellationToken.None);

        has.ShouldBeTrue();
    }

    private static ChunkRecord TextChunk(string project, string id, string content, ReadOnlyMemory<float> embedding) => new()
    {
        Id = $"{project}:{id}",
        Project = project,
        Path = $"{id}.cs",
        Kind = "code",
        Symbol = string.Empty,
        StartLine = 1,
        EndLine = 10,
        Content = content,
        Embedding = embedding,
    };

    [Fact]
    public async Task SearchTextAsync_ReadsWhileAnotherConnectionHoldsAnOpenWriteTransaction()
    {
        var store = new SqliteProjectKnowledgeStore($"{ConnectionString};Default Timeout=2");
        await store.InitializeAsync(CancellationToken.None);
        await store.ReplaceProjectAsync(
            "alpha",
            new[] { TextChunk("alpha", "ledger", "ledger entry", ReadOnlyMemory<float>.Empty) },
            CancellationToken.None);

        await using var writer = new SqliteConnection(ConnectionString);
        await writer.OpenAsync();
        await using (var write = writer.CreateCommand())
        {
            write.CommandText = "BEGIN EXCLUSIVE; DELETE FROM chunk_fts WHERE project = 'beta';";
            await write.ExecuteNonQueryAsync();
        }

        IReadOnlyList<ChunkRecord> hits = Array.Empty<ChunkRecord>();
        await Should.NotThrowAsync(async () =>
            hits = await store.SearchTextAsync("alpha", "ledger", top: 5, CancellationToken.None));

        hits.ShouldHaveSingleItem().Id.ShouldBe("alpha:ledger");
    }

    [Fact]
    public async Task InitializeAsync_CreatesTheFullTextTable()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);

        await store.InitializeAsync(CancellationToken.None);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'chunk_fts';";
        var sql = (string?)await command.ExecuteScalarAsync();
        sql.ShouldNotBeNull().ShouldContain("fts5", Case.Insensitive);
    }

    [Fact]
    public async Task HasProjectAsync_IsTrueForAProjectIndexedWithoutVectors()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "owner/repo",
            new[] { TextChunk("owner/repo", "only", "public class Invoice { }", ReadOnlyMemory<float>.Empty) },
            CancellationToken.None);

        var has = await store.HasProjectAsync("owner/repo", CancellationToken.None);

        has.ShouldBeTrue();
    }

    [Fact]
    public async Task ReplaceProjectAsync_WritesVectorlessChunksToTheTextTableOnly()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await Should.NotThrowAsync(() => store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                TextChunk("alpha", "vector", "ledger entry", Vector((0, 1f))),
                TextChunk("alpha", "vectorless", "ledger posting", ReadOnlyMemory<float>.Empty),
            },
            CancellationToken.None));

        var vectorHits = await store.SearchAsync("alpha", Vector((0, 1f)), top: 10, CancellationToken.None);
        vectorHits.ShouldHaveSingleItem().Id.ShouldBe("alpha:vector");

        var textHits = await store.SearchTextAsync("alpha", "posting", top: 10, CancellationToken.None);
        textHits.ShouldHaveSingleItem().Id.ShouldBe("alpha:vectorless");
    }

    [Fact]
    public async Task ReplaceProjectAsync_WhenTheVectorWriteFails_LeavesTheProjectUnindexedAndThrows()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await Should.ThrowAsync<VectorStoreException>(() => store.ReplaceProjectAsync(
            "alpha",
            new[] { TextChunk("alpha", "wrong-size", "ledger entry", WrongSizeVector) },
            CancellationToken.None));

        (await store.HasProjectAsync("alpha", CancellationToken.None)).ShouldBeFalse();
        (await store.SearchTextAsync("alpha", "ledger", top: 5, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplaceFileAsync_WhenTheVectorWriteFails_KeepsTheTextRowsItHad()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);
        await store.ReplaceProjectAsync(
            "alpha",
            new[] { FileChunk("alpha", "Foo.cs", "old", Vector((0, 1f))) },
            CancellationToken.None);

        await Should.ThrowAsync<VectorStoreException>(() => store.ReplaceFileAsync(
            "alpha",
            "Foo.cs",
            new[] { FileChunk("alpha", "Foo.cs", "new", WrongSizeVector) },
            CancellationToken.None));

        (await store.FindSymbolAsync("alpha", "old", kind: null, top: 5, CancellationToken.None))
            .ShouldHaveSingleItem().Id.ShouldBe("alpha:Foo.cs:old");
        (await store.FindSymbolAsync("alpha", "new", kind: null, top: 5, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchTextAsync_RanksTheChunkWithTheQuestionsWordsFirst()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                TextChunk("alpha", "weak", "the invoice header and the customer address", ReadOnlyMemory<float>.Empty),
                TextChunk("alpha", "strong", "invoice total applies the discount to every invoice line", ReadOnlyMemory<float>.Empty),
                TextChunk("alpha", "none", "the shipping label printer", ReadOnlyMemory<float>.Empty),
            },
            CancellationToken.None);

        var hits = await store.SearchTextAsync("alpha", "How is the invoice total discount computed?", top: 5, CancellationToken.None);

        hits.Select(h => h.Id).ShouldBe(["alpha:strong", "alpha:weak", "alpha:none"]);
        hits[0].Content.ShouldBe("invoice total applies the discount to every invoice line");
        hits[0].Embedding.Length.ShouldBe(0);
    }

    [Fact]
    public async Task SearchTextAsync_WithOnlyTwoLetterWords_ReturnsNothing()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);
        await store.ReplaceProjectAsync(
            "alpha",
            new[] { TextChunk("alpha", "notes", "it is up to us to do so", ReadOnlyMemory<float>.Empty) },
            CancellationToken.None);

        var hits = await store.SearchTextAsync("alpha", "Is it up to us?", top: 5, CancellationToken.None);

        hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchTextAsync_NeverReturnsAnotherProjectsChunks()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);

        await store.ReplaceProjectAsync(
            "alpha",
            new[] { TextChunk("alpha", "mine", "refund policy", ReadOnlyMemory<float>.Empty) },
            CancellationToken.None);
        await store.ReplaceProjectAsync(
            "beta",
            new[] { TextChunk("beta", "theirs", "refund policy refund refund", ReadOnlyMemory<float>.Empty) },
            CancellationToken.None);

        var hits = await store.SearchTextAsync("alpha", "refund policy", top: 10, CancellationToken.None);

        hits.ShouldHaveSingleItem().Id.ShouldBe("alpha:mine");
    }

    [Theory]
    [InlineData("\"invoice", 1)]
    [InlineData("(invoice)", 1)]
    [InlineData("invoice AND", 1)]
    [InlineData("NEAR(invoice total)", 1)]
    [InlineData("invoice*", 1)]
    [InlineData("path:invoice", 1)]
    [InlineData("NOT invoice", 1)]
    [InlineData("AND OR NOT", 0)]
    [InlineData("*", 0)]
    [InlineData("\"", 0)]
    [InlineData(":", 0)]
    [InlineData("", 0)]
    public async Task SearchTextAsync_QuotesOperatorsAndNeverThrows(string text, int expected)
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);
        await store.ReplaceProjectAsync(
            "alpha",
            new[] { TextChunk("alpha", "billing", "invoice total", ReadOnlyMemory<float>.Empty) },
            CancellationToken.None);

        IReadOnlyList<ChunkRecord> hits = Array.Empty<ChunkRecord>();
        await Should.NotThrowAsync(async () =>
            hits = await store.SearchTextAsync("alpha", text, top: 5, CancellationToken.None));

        hits.Count.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Async", "SearchAsync")]
    [InlineData("Symbol", "FindSymbolAsync")]
    public async Task FindSymbolAsync_FindsTheNameInsideTheSymbol(string name, string expected)
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);
        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                SymbolChunk("alpha", "a", "SearchAsync", "code"),
                SymbolChunk("alpha", "b", "FindSymbolAsync", "code"),
                SymbolChunk("alpha", "c", "Unrelated", "code"),
            },
            CancellationToken.None);

        var results = await store.FindSymbolAsync("alpha", name, kind: null, top: 10, CancellationToken.None);

        results.Select(r => r.Symbol).ShouldContain(expected);
        results.ShouldNotContain(r => r.Symbol == "Unrelated");
    }

    [Theory]
    [InlineData("my_func", "my_func")]
    [InlineData("100%", "100%done")]
    [InlineData("a\\b", "a\\b")]
    public async Task FindSymbolAsync_MatchesPercentUnderscoreAndBackslashLiterally(string name, string expected)
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);
        await store.ReplaceProjectAsync(
            "alpha",
            new[]
            {
                SymbolChunk("alpha", "a", "my_func", "code"),
                SymbolChunk("alpha", "b", "myXfunc", "code"),
                SymbolChunk("alpha", "c", "100%done", "code"),
                SymbolChunk("alpha", "d", "100Xdone", "code"),
                SymbolChunk("alpha", "e", "a\\b", "code"),
                SymbolChunk("alpha", "f", "ab", "code"),
            },
            CancellationToken.None);

        var results = await store.FindSymbolAsync("alpha", name, kind: null, top: 10, CancellationToken.None);

        results.ShouldHaveSingleItem().Symbol.ShouldBe(expected);
    }

    [Theory]
    [InlineData("\"*")]
    [InlineData("Handler\" OR symbol:*")]
    public async Task FindSymbolAsync_WithOperatorsInTheName_ReturnsEmptyWithoutThrowing(string name)
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);
        await store.ReplaceProjectAsync(
            "alpha",
            new[] { SymbolChunk("alpha", "a", "Handler", "code") },
            CancellationToken.None);

        IReadOnlyList<ChunkRecord> results = Array.Empty<ChunkRecord>();
        await Should.NotThrowAsync(async () =>
            results = await store.FindSymbolAsync("alpha", name, kind: null, top: 5, CancellationToken.None));

        results.ShouldBeEmpty();
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
