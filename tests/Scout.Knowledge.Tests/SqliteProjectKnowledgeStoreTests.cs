namespace Scout.Knowledge.Tests;

public sealed class SqliteProjectKnowledgeStoreTests : IDisposable
{
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

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
