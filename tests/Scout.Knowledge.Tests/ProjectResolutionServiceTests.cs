using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;

namespace Scout.Knowledge.Tests;

public sealed class ProjectResolutionServiceTests : IDisposable
{
    private readonly string _root;

    public ProjectResolutionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scout-resolution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string MakeRepo(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        return dir;
    }

    private static ProjectIndexer RealIndexer(IProjectKnowledgeStore store) =>
        new(
            Substitute.For<IChunker>(),
            Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            store,
            NullLogger<ProjectIndexer>.Instance);

    private static ProjectResolutionService NewService(
        ProjectsOptions options,
        IProjectKnowledgeStore store,
        ProjectIndexer? indexer = null)
    {
        var discovery = new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance);
        return new ProjectResolutionService(
            Options.Create(options),
            discovery,
            indexer ?? RealIndexer(store),
            store,
            NullLogger<ProjectResolutionService>.Instance);
    }

    [Fact]
    public void GetProjectNames_MergesDiscoveredAndExplicit()
    {
        MakeRepo("discovered-app");
        var options = new ProjectsOptions
        {
            Roots = { _root },
            Items = { new ProjectEntry { Name = "explicit-app", RepoPath = "/tmp/explicit" } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();

        var names = NewService(options, store).GetProjectNames();

        names.ShouldContain("discovered-app");
        names.ShouldContain("explicit-app");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitWinsOnCollision()
    {
        var discoveredRepo = MakeRepo("collide");
        var options = new ProjectsOptions
        {
            Roots = { _root },
            Items = { new ProjectEntry { Name = "collide", RepoPath = "/explicit/path" } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("collide", Arg.Any<CancellationToken>()).Returns(true);

        var entry = await NewService(options, store).ResolveAsync("collide");

        entry.ShouldNotBeNull();
        entry!.RepoPath.ShouldBe("/explicit/path");
        entry.RepoPath.ShouldNotBe(discoveredRepo);
    }

    [Fact]
    public async Task ResolveAsync_LazyIndexesOnFirstUse()
    {
        // Empty existing repo dir: indexing finds no files and replaces the project
        // with an empty set, so ReplaceProjectAsync is still invoked exactly once.
        var repo = MakeRepo("lazy");
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "lazy", RepoPath = repo } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("lazy", Arg.Any<CancellationToken>()).Returns(false);

        var entry = await NewService(options, store).ResolveAsync("lazy");

        entry.ShouldNotBeNull();
        await store.Received(1).ReplaceProjectAsync(
            "lazy",
            Arg.Any<IReadOnlyList<ChunkRecord>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_SkipsReindexWhenAlreadyIndexed()
    {
        var repo = MakeRepo("cached");
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "cached", RepoPath = repo } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("cached", Arg.Any<CancellationToken>()).Returns(true);

        var entry = await NewService(options, store).ResolveAsync("cached");

        entry.ShouldNotBeNull();
        await store.DidNotReceive().ReplaceProjectAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ChunkRecord>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_UnknownProject_ReturnsNull()
    {
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "known", RepoPath = "/tmp/known" } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();

        var entry = await NewService(options, store).ResolveAsync("ghost");

        entry.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_IndexerFailure_ReturnsNull()
    {
        // Missing repo path: ProjectIndexer.IndexAsync enumerates files and throws
        // DirectoryNotFoundException, which ResolveAsync catches and maps to null.
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "boom", RepoPath = Path.Combine(_root, "does-not-exist") } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("boom", Arg.Any<CancellationToken>()).Returns(false);

        var entry = await NewService(options, store).ResolveAsync("boom");

        entry.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_WhenTheVectorWriteFails_ReturnsNullAndIndexesAgainOnTheNextUse()
    {
        var repo = MakeRepo("half");
        File.WriteAllText(Path.Combine(repo, "README.md"), "Ledger entries are posted nightly.");
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("half", Arg.Any<CancellationToken>()).Returns(false);
        store.ReplaceProjectAsync("half", Arg.Any<IReadOnlyList<ChunkRecord>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new VectorStoreException("The vector write failed.")));
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new GeneratedEmbeddings<Embedding<float>> { new Embedding<float>(new float[ChunkRecord.EmbeddingDimensions]) });
        var indexer = new ProjectIndexer(new FileChunker(), embedder, store, NullLogger<ProjectIndexer>.Instance);
        var service = NewService(
            new ProjectsOptions { Items = { new ProjectEntry { Name = "half", RepoPath = repo } } },
            store,
            indexer);

        var first = await service.ResolveAsync("half");
        var second = await service.ResolveAsync("half");

        first.ShouldBeNull();
        second.ShouldBeNull();
        await store.Received(2).ReplaceProjectAsync("half", Arg.Any<IReadOnlyList<ChunkRecord>>(), Arg.Any<CancellationToken>());
        await embedder.Received(2).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_OnADatabaseWithVectorsOnly_FillsTheTextIndexWithoutEmbedding()
    {
        var repo = MakeRepo("legacy");
        File.WriteAllText(Path.Combine(repo, "README.md"), "Shipping labels are printed by the warehouse.");
        var connectionString = $"Data Source={Path.Combine(_root, "legacy.db")}";
        var vector = new float[768];
        vector[0] = 1f;
        var legacy = new SqliteVectorStore(connectionString).GetCollection<string, ChunkRecord>("project_chunks");
        await legacy.EnsureCollectionExistsAsync();
        await legacy.UpsertAsync(new ChunkRecord
        {
            Id = "legacy:Ledger.cs:3",
            Project = "legacy",
            Path = "Ledger.cs",
            Kind = "code",
            Symbol = "PostEntries",
            StartLine = 3,
            EndLine = 9,
            Content = "Ledger entries are posted nightly.",
            Embedding = vector,
        });
        var store = new SqliteProjectKnowledgeStore(connectionString);
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var service = NewService(
            new ProjectsOptions { Items = { new ProjectEntry { Name = "legacy", RepoPath = repo } } },
            store,
            new ProjectIndexer(new FileChunker(), embedder, store, NullLogger<ProjectIndexer>.Instance));

        var entry = await service.ResolveAsync("legacy");

        entry.ShouldNotBeNull().Name.ShouldBe("legacy");
        var hit = (await store.SearchTextAsync("legacy", "ledger entries", top: 5, CancellationToken.None)).ShouldHaveSingleItem();
        hit.Id.ShouldBe("legacy:Ledger.cs:3");
        hit.Symbol.ShouldBe("PostEntries");
        hit.StartLine.ShouldBe(3);
        (await store.FindSymbolAsync("legacy", "PostEntries", kind: "code", top: 5, CancellationToken.None))
            .ShouldHaveSingleItem().Path.ShouldBe("Ledger.cs");
        (await store.GetIndexedPathAsync("legacy", CancellationToken.None)).ShouldBe(repo);
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    private static IEmbeddingGenerator<string, Embedding<float>> NoEmbeddings()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<GeneratedEmbeddings<Embedding<float>>>(_ => throw new NotSupportedException("Embeddings are not available."));
        return embedder;
    }

    private string MakeRepoWithClass(string name, string className)
    {
        var repo = MakeRepo(name);
        File.WriteAllText(Path.Combine(repo, "Ledger.cs"), $"public class {className}\n{{\n}}\n");
        return repo;
    }

    private static ProjectResolutionService ServiceAt(
        string project,
        string repo,
        SqliteProjectKnowledgeStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ILogger<ProjectResolutionService>? logger = null)
        => new(
            Options.Create(new ProjectsOptions { Items = { new ProjectEntry { Name = project, RepoPath = repo } } }),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            new ProjectIndexer(new FileChunker(), embedder, store, NullLogger<ProjectIndexer>.Instance),
            store,
            logger ?? NullLogger<ProjectResolutionService>.Instance);

    private static async Task<string[]> SymbolsAsync(SqliteProjectKnowledgeStore store, string project)
        => [.. (await store.FindSymbolAsync(project, "Ledger", kind: null, top: 0, CancellationToken.None)).Select(r => r.Symbol)];

    [Fact]
    public async Task ResolveAsync_On_A_Project_Whose_Configured_Path_Changed_Since_It_Was_Indexed_Indexes_The_New_Path_Before_Answering()
    {
        var before = MakeRepoWithClass("before", "LedgerBefore");
        var after = MakeRepoWithClass("after", "LedgerAfter");
        var store = new SqliteProjectKnowledgeStore($"Data Source={Path.Combine(_root, "moved.db")}");
        (await ServiceAt("ledger", before, store, NoEmbeddings()).ResolveAsync("ledger")).ShouldNotBeNull();
        (await SymbolsAsync(store, "ledger")).ShouldBe(["LedgerBefore"]);
        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(before);

        var entry = await ServiceAt("ledger", after, store, NoEmbeddings()).ResolveAsync("ledger");

        entry.ShouldNotBeNull().RepoPath.ShouldBe(after);
        (await SymbolsAsync(store, "ledger")).ShouldBe(["LedgerAfter"]);
        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(after);
    }

    [Fact]
    public async Task ResolveAsync_On_A_Project_Indexed_From_The_Configured_Path_By_An_Earlier_Run_Does_Not_Index_It_Again()
    {
        var repo = MakeRepoWithClass("same", "LedgerSame");
        var store = new SqliteProjectKnowledgeStore($"Data Source={Path.Combine(_root, "same.db")}");
        (await ServiceAt("ledger", repo, store, NoEmbeddings()).ResolveAsync("ledger")).ShouldNotBeNull();
        File.WriteAllText(Path.Combine(repo, "Ledger.cs"), "public class LedgerEdited\n{\n}\n");
        var embedder = NoEmbeddings();

        var entry = await ServiceAt("ledger", repo, store, embedder).ResolveAsync("ledger");

        entry.ShouldNotBeNull().RepoPath.ShouldBe(repo);
        (await SymbolsAsync(store, "ledger")).ShouldBe(["LedgerSame"]);
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    private async Task<string> DatabaseWrittenBeforeThePathWasRecordedAsync(string fileName)
    {
        var connectionString = $"Data Source={Path.Combine(_root, fileName)}";
        await using var old = new SqliteConnection(connectionString);
        await old.OpenAsync();
        await using var schema = old.CreateCommand();
        schema.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE VIRTUAL TABLE chunk_fts USING fts5(
                id UNINDEXED, project UNINDEXED, path, kind UNINDEXED, symbol, start_line UNINDEXED, end_line UNINDEXED, content,
                tokenize = 'unicode61');
            INSERT INTO chunk_fts (id, project, path, kind, symbol, start_line, end_line, content)
            VALUES ('ledger:Ledger.cs:1', 'ledger', 'Ledger.cs', 'code', 'LedgerInTheOldIndex', 1, 3, 'public class LedgerInTheOldIndex');
            """;
        await schema.ExecuteNonQueryAsync();
        (await TablesAsync(old)).ShouldNotContain("project_source");
        return connectionString;
    }

    [Fact]
    public async Task ResolveAsync_On_A_Database_Written_Before_The_Path_Was_Recorded_Adopts_The_Configured_Path_Without_Indexing_And_Records_It()
    {
        var repo = MakeRepoWithClass("adopted", "LedgerOnDisk");
        var connectionString = await DatabaseWrittenBeforeThePathWasRecordedAsync("old-shape.db");
        var store = new SqliteProjectKnowledgeStore(connectionString);
        var embedder = NoEmbeddings();

        var entry = await ServiceAt("ledger", repo, store, embedder).ResolveAsync("ledger");

        entry.ShouldNotBeNull().RepoPath.ShouldBe(repo);
        (await SymbolsAsync(store, "ledger")).ShouldBe(["LedgerInTheOldIndex"]);
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(repo);
        await using var reopened = new SqliteConnection(connectionString);
        await reopened.OpenAsync();
        (await TablesAsync(reopened)).ShouldContain("project_source");
    }

    [Fact]
    public async Task ResolveAsync_On_A_Database_Written_Before_The_Path_Was_Recorded_Indexes_Again_Once_The_Path_It_Adopted_Changes()
    {
        var adopted = MakeRepoWithClass("adopted-first", "LedgerOnDisk");
        var moved = MakeRepoWithClass("adopted-then-moved", "LedgerAfterTheMove");
        var store = new SqliteProjectKnowledgeStore(await DatabaseWrittenBeforeThePathWasRecordedAsync("old-shape-moved.db"));
        (await ServiceAt("ledger", adopted, store, NoEmbeddings()).ResolveAsync("ledger")).ShouldNotBeNull();

        var entry = await ServiceAt("ledger", moved, store, NoEmbeddings()).ResolveAsync("ledger");

        entry.ShouldNotBeNull().RepoPath.ShouldBe(moved);
        (await SymbolsAsync(store, "ledger")).ShouldBe(["LedgerAfterTheMove"]);
        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(moved);
    }

    private static async Task<List<string>> TablesAsync(SqliteConnection connection)
    {
        var names = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    [Fact]
    public async Task A_Project_Registered_At_Another_Path_While_Its_Old_Path_Is_Indexing_Stays_Stale_And_Is_Indexed_Again_On_Its_Next_Use()
    {
        var first = MakeRepo("first-tree");
        File.WriteAllText(Path.Combine(first, "README.md"), "The first tree.");
        var second = MakeRepo("second-tree");
        File.WriteAllText(Path.Combine(second, "README.md"), "The second tree.");
        var replaced = new List<string>();
        var firstReplaceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReplace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("octocat/hello", Arg.Any<CancellationToken>()).Returns(_ => replaced.Count > 0);
        store.GetIndexedPathAsync("octocat/hello", Arg.Any<CancellationToken>()).Returns((string?)null);
        store.ReplaceProjectAsync("octocat/hello", Arg.Any<IReadOnlyList<ChunkRecord>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var content = string.Join(" | ", call.Arg<IReadOnlyList<ChunkRecord>>().Select(record => record.Content));
                if (replaced.Count == 0)
                {
                    firstReplaceStarted.SetResult();
                    await releaseFirstReplace.Task;
                }

                replaced.Add(content);
            });
        var service = NewService(
            new ProjectsOptions(),
            store,
            new ProjectIndexer(new FileChunker(), NoEmbeddings(), store, NullLogger<ProjectIndexer>.Instance));
        service.Register(new ProjectEntry { Name = "octocat/hello", RepoPath = first });

        var oldRun = service.ResolveAsync("octocat/hello");
        await firstReplaceStarted.Task;
        service.Register(new ProjectEntry { Name = "octocat/hello", RepoPath = second });
        releaseFirstReplace.SetResult();
        (await oldRun).ShouldNotBeNull().RepoPath.ShouldBe(first);

        var next = await service.ResolveAsync("octocat/hello");

        next.ShouldNotBeNull().RepoPath.ShouldBe(second);
        replaced.ShouldBe(["The first tree.", "The second tree."]);
        (await service.ResolveAsync("octocat/hello")).ShouldNotBeNull();
        replaced.Count.ShouldBe(2);
    }

    private sealed class SwitchableEmbedder
    {
        public SwitchableEmbedder()
        {
            var vector = new float[ChunkRecord.EmbeddingDimensions];
            vector[0] = 1f;
            Generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
            Generator
                .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
                .Returns(call => Down
                    ? Task.FromException<GeneratedEmbeddings<Embedding<float>>>(Failure)
                    : Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                        ((IEnumerable<string>)call[0]).Select(_ => new Embedding<float>(vector)))));
        }

        public IEmbeddingGenerator<string, Embedding<float>> Generator { get; }

        public bool Down { get; set; }

        public Exception Failure { get; set; } = new HttpRequestException("Connection refused");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Project_Whose_Path_Changed_And_Whose_Index_Run_Fails_Says_That_Indexing_Failed_Answers_Nothing_From_The_Old_Path_And_Indexes_Again_On_Its_Next_Use(
        bool registeredAtRunTime)
    {
        var before = MakeRepoWithClass("old-path", "LedgerBefore");
        var after = MakeRepoWithClass("new-path", "LedgerAfter");
        var store = new SqliteProjectKnowledgeStore($"Data Source={Path.Combine(_root, "changed-and-down.db")}");
        var embedder = new SwitchableEmbedder();
        var logger = new RecordingLogger();
        var resolution = ServiceAt("ledger", before, store, embedder.Generator);
        (await resolution.ResolveAsync("ledger")).ShouldNotBeNull();
        if (registeredAtRunTime)
        {
            resolution = ServiceAt("ledger", before, store, embedder.Generator, logger);
            resolution.Register(new ProjectEntry { Name = "ledger", RepoPath = after });
        }
        else
        {
            resolution = ServiceAt("ledger", after, store, embedder.Generator, logger);
        }

        var tools = new ProjectKnowledgeService(embedder.Generator, store, resolution, NullLogger<ProjectKnowledgeService>.Instance);
        embedder.Down = true;

        var whileDown = await tools.FindSymbolAsync("ledger", "Ledger", kind: null, CancellationToken.None);

        whileDown.ShouldBe("Indexing project 'ledger' failed, so it cannot be searched yet. Try again later.");
        var error = logger.Entries.ShouldHaveSingleItem();
        error.Level.ShouldBe(LogLevel.Error);
        error.Message.ShouldBe("Indexing failed for project ledger, which has no index of its current path to answer from.");
        error.Exception.ShouldBeOfType<HttpRequestException>();

        embedder.Down = false;
        var onceBack = await tools.FindSymbolAsync("ledger", "Ledger", kind: null, CancellationToken.None);

        onceBack.ShouldStartWith("[Ledger.cs:1-3 LedgerAfter]");
        onceBack.ShouldNotContain("LedgerBefore");
        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(after);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task A_Project_Registered_Again_At_The_Path_It_Was_Indexed_From_Whose_Index_Run_Fails_Answers_From_Its_Index_Logs_And_Indexes_Again_On_Its_Next_Use(
        Type failure)
    {
        var repo = MakeRepoWithClass("same-place", "LedgerBefore");
        var store = new SqliteProjectKnowledgeStore($"Data Source={Path.Combine(_root, "same-and-down.db")}");
        var embedder = new SwitchableEmbedder { Failure = (Exception)Activator.CreateInstance(failure)! };
        (await ServiceAt("octocat/ledger", repo, store, embedder.Generator).ResolveAsync("octocat/ledger")).ShouldNotBeNull();
        File.WriteAllText(Path.Combine(repo, "Ledger.cs"), "public class LedgerRetaken\n{\n}\n");
        var logger = new RecordingLogger();
        var restarted = new ProjectResolutionService(
            Options.Create(new ProjectsOptions()),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            new ProjectIndexer(new FileChunker(), embedder.Generator, store, NullLogger<ProjectIndexer>.Instance),
            store,
            logger);
        restarted.Register(new ProjectEntry { Name = "octocat/ledger", RepoPath = repo });
        var tools = new ProjectKnowledgeService(embedder.Generator, store, restarted, NullLogger<ProjectKnowledgeService>.Instance);
        embedder.Down = true;

        var whileDown = await tools.FindSymbolAsync("octocat/ledger", "Ledger", kind: null, CancellationToken.None);

        whileDown.ShouldStartWith("[Ledger.cs:1-3 LedgerBefore]");
        var error = logger.Entries.ShouldHaveSingleItem();
        error.Level.ShouldBe(LogLevel.Error);
        error.Message.ShouldBe("Indexing failed for project octocat/ledger; answering from the index it already has.");
        error.Exception.ShouldBeOfType(failure);

        embedder.Down = false;
        (await tools.FindSymbolAsync("octocat/ledger", "Ledger", kind: null, CancellationToken.None))
            .ShouldStartWith("[Ledger.cs:1-3 LedgerRetaken]");
    }

    [Fact]
    public async Task ResolveAsync_When_Indexing_Fails_And_There_Is_No_Index_Returns_Null_And_Logs_That_Nothing_Can_Answer()
    {
        var repo = MakeRepoWithClass("never-indexed", "Ledger");
        var store = new SqliteProjectKnowledgeStore($"Data Source={Path.Combine(_root, "none.db")}");
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new HttpRequestException("Connection refused")));
        var logger = new RecordingLogger();

        var entry = await ServiceAt("ledger", repo, store, embedder, logger).ResolveAsync("ledger");

        entry.ShouldBeNull();
        (await store.HasProjectAsync("ledger", CancellationToken.None)).ShouldBeFalse();
        var error = logger.Entries.ShouldHaveSingleItem();
        error.Level.ShouldBe(LogLevel.Error);
        error.Message.ShouldBe("Indexing failed for project ledger, which has no index of its current path to answer from.");
        error.Exception.ShouldBeOfType<HttpRequestException>();
    }

    private sealed class RecordingLogger : ILogger<ProjectResolutionService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public async Task Construction_DoesNotIndex()
    {
        MakeRepo("startup-app");
        var options = new ProjectsOptions { Roots = { _root } };
        var store = Substitute.For<IProjectKnowledgeStore>();

        _ = NewService(options, store);

        // No resolution requested yet, so nothing must be indexed at startup.
        await store.DidNotReceive().ReplaceProjectAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ChunkRecord>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Project_Registered_At_Run_Time_Is_Listed_Found_Indexed_Once_For_The_Same_Path_And_Again_When_Its_Path_Changes()
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("octocat/hello", Arg.Any<CancellationToken>()).Returns(true);
        var indexer = RealIndexer(store);
        var service = NewService(new ProjectsOptions(), store, indexer);
        var first = MakeRepo("first");
        var second = MakeRepo("second");

        service.GetProjectNames().ShouldBeEmpty();
        service.Register(new ProjectEntry { Name = "octocat/hello", RepoPath = first });
        service.GetProjectNames().ShouldBe(["octocat/hello"]);
        service.Find("octocat/hello").ShouldNotBeNull().RepoPath.ShouldBe(first);

        (await service.ResolveAsync("octocat/hello")).ShouldNotBeNull();
        (await service.ResolveAsync("octocat/hello")).ShouldNotBeNull();
        service.Register(new ProjectEntry { Name = "octocat/hello", RepoPath = first });
        (await service.ResolveAsync("octocat/hello")).ShouldNotBeNull();
        await store.Received(1).ReplaceProjectAsync("octocat/hello", Arg.Any<IReadOnlyList<ChunkRecord>>(), Arg.Any<CancellationToken>());

        service.Register(new ProjectEntry { Name = "octocat/hello", RepoPath = second });
        (await service.ResolveAsync("octocat/hello")).ShouldNotBeNull().RepoPath.ShouldBe(second);
        await store.Received(2).ReplaceProjectAsync("octocat/hello", Arg.Any<IReadOnlyList<ChunkRecord>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_Project_Without_A_Name_Or_A_Path_Cannot_Be_Registered()
    {
        var service = NewService(new ProjectsOptions(), Substitute.For<IProjectKnowledgeStore>());

        Should.Throw<ArgumentException>(() => service.Register(new ProjectEntry { Name = "", RepoPath = "/x" }));
        Should.Throw<ArgumentException>(() => service.Register(new ProjectEntry { Name = "x", RepoPath = " " }));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static ProjectKnowledgeService Tools(
        ProjectResolutionService resolution,
        SqliteProjectKnowledgeStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder)
        => new(embedder, store, resolution, NullLogger<ProjectKnowledgeService>.Instance);

    [Fact]
    public async Task A_First_Index_That_Stopped_Between_The_Vector_Write_And_The_Text_Write_Is_Indexed_Again_On_The_Next_Resolve_And_A_Field_Is_Then_Found()
    {
        var repo = MakeRepo("interrupted");
        File.WriteAllText(Path.Combine(repo, "Ledger.cs"), "public class Ledger\n{\n    private int _balance;\n}\n");
        var connectionString = $"Data Source={Path.Combine(_root, "interrupted.db")}";
        var store = new SqliteProjectKnowledgeStore(connectionString);
        await store.InitializeAsync(CancellationToken.None);
        await ExecuteAsync(
            connectionString,
            "CREATE TRIGGER text_write_fails BEFORE INSERT ON chunk_fts_content BEGIN SELECT RAISE(ABORT, 'disk full'); END;");
        var embedder = new SwitchableEmbedder().Generator;
        (await Tools(ServiceAt("ledger", repo, store, embedder), store, embedder)
            .FindSymbolAsync("ledger", "_balance", kind: null, CancellationToken.None))
            .ShouldBe("Indexing project 'ledger' failed, so it cannot be searched yet. Try again later.");
        var query = new float[ChunkRecord.EmbeddingDimensions];
        query[0] = 1f;
        (await store.SearchAsync("ledger", query, top: 5, CancellationToken.None)).ShouldHaveSingleItem().Symbol.ShouldBe("Ledger");
        await ExecuteAsync(connectionString, "DROP TRIGGER text_write_fails;");

        var found = await Tools(ServiceAt("ledger", repo, store, embedder), store, embedder)
            .FindSymbolAsync("ledger", "_balance", kind: null, CancellationToken.None);

        found.ShouldBe("[Ledger.cs:3-3 _balance]\nprivate int _balance;");
        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(repo);
    }

    [Fact]
    public async Task ResolveAsync_On_A_Complete_Index_Of_Vectors_And_Text_Rows_Written_Before_The_Path_Was_Recorded_Adopts_It_Without_Embedding_Again()
    {
        var repo = MakeRepoWithClass("complete-before", "LedgerOnDisk");
        var connectionString = await DatabaseWrittenBeforeThePathWasRecordedAsync("complete-before.db");
        var vector = new float[ChunkRecord.EmbeddingDimensions];
        vector[0] = 1f;
        var vectors = new SqliteVectorStore(connectionString).GetCollection<string, ChunkRecord>("project_chunks");
        await vectors.EnsureCollectionExistsAsync();
        await vectors.UpsertAsync(new ChunkRecord
        {
            Id = "ledger:Ledger.cs:1",
            Project = "ledger",
            Path = "Ledger.cs",
            Kind = "code",
            Symbol = "LedgerInTheOldIndex",
            StartLine = 1,
            EndLine = 3,
            Content = "public class LedgerInTheOldIndex",
            Embedding = vector,
        });
        var store = new SqliteProjectKnowledgeStore(connectionString);
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var entry = await ServiceAt("ledger", repo, store, embedder).ResolveAsync("ledger");

        entry.ShouldNotBeNull().RepoPath.ShouldBe(repo);
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        (await SymbolsAsync(store, "ledger")).ShouldBe(["LedgerInTheOldIndex"]);
        (await store.SearchAsync("ledger", vector, top: 5, CancellationToken.None)).ShouldHaveSingleItem().Id.ShouldBe("ledger:Ledger.cs:1");
        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(repo);
    }

    private static Task RefuseToRecordAPathAsync(string connectionString)
        => ExecuteAsync(
            connectionString,
            """
            CREATE TRIGGER path_insert_fails BEFORE INSERT ON project_source WHEN NEW.repo_path <> ''
            BEGIN SELECT RAISE(ABORT, 'disk full'); END;
            CREATE TRIGGER path_update_fails BEFORE UPDATE ON project_source WHEN NEW.repo_path <> ''
            BEGIN SELECT RAISE(ABORT, 'disk full'); END;
            """);

    private static Task RecordPathsAgainAsync(string connectionString)
        => ExecuteAsync(connectionString, "DROP TRIGGER path_insert_fails; DROP TRIGGER path_update_fails;");

    [Fact]
    public async Task An_Index_Written_Before_The_Path_Was_Recorded_Whose_Path_Cannot_Be_Recorded_Still_Answers_Warns_Once_And_Records_The_Path_On_A_Later_Resolve()
    {
        var repo = MakeRepoWithClass("adopt-refused", "LedgerOnDisk");
        var connectionString = await DatabaseWrittenBeforeThePathWasRecordedAsync("adopt-refused.db");
        var store = new SqliteProjectKnowledgeStore(connectionString);
        await store.InitializeAsync(CancellationToken.None);
        await RefuseToRecordAPathAsync(connectionString);
        var embedder = NoEmbeddings();
        var logger = new RecordingLogger();
        var resolution = ServiceAt("ledger", repo, store, embedder, logger);

        var whileRefused = await Tools(resolution, store, embedder).FindSymbolAsync("ledger", "Ledger", kind: null, CancellationToken.None);

        whileRefused.ShouldBe("[Ledger.cs:1-3 LedgerInTheOldIndex]\npublic class LedgerInTheOldIndex");
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe("Could not record the path project ledger is indexed from; it is recorded on a later use.");
        warning.Exception.ShouldBeOfType<SqliteException>();
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBeNull();

        await RecordPathsAgainAsync(connectionString);
        (await resolution.ResolveAsync("ledger")).ShouldNotBeNull();

        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(repo);
    }

    [Fact]
    public async Task A_First_Index_Whose_Path_Cannot_Be_Recorded_Still_Answers_And_Records_The_Path_On_A_Later_Resolve_Without_Indexing_Again()
    {
        var repo = MakeRepoWithClass("record-refused", "LedgerFresh");
        var connectionString = $"Data Source={Path.Combine(_root, "record-refused.db")}";
        var store = new SqliteProjectKnowledgeStore(connectionString);
        await store.InitializeAsync(CancellationToken.None);
        await RefuseToRecordAPathAsync(connectionString);
        var embedder = new SwitchableEmbedder().Generator;
        var resolution = ServiceAt("ledger", repo, store, embedder);

        var whileRefused = await Tools(resolution, store, embedder).FindSymbolAsync("ledger", "Ledger", kind: null, CancellationToken.None);

        whileRefused.ShouldBe("[Ledger.cs:1-3 LedgerFresh]\npublic class LedgerFresh\n{\n}");
        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(string.Empty);

        await RecordPathsAgainAsync(connectionString);
        (await resolution.ResolveAsync("ledger")).ShouldNotBeNull();

        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(repo);
        await embedder.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Project_Indexed_Again_At_A_Changed_Path_Whose_Path_Cannot_Be_Recorded_Answers_From_The_New_Path_And_Is_Indexed_Again_On_Its_Next_Use()
    {
        var before = MakeRepoWithClass("moved-from", "LedgerBefore");
        var after = MakeRepoWithClass("moved-to", "LedgerAfter");
        var connectionString = $"Data Source={Path.Combine(_root, "moved-refused.db")}";
        var store = new SqliteProjectKnowledgeStore(connectionString);
        var embedder = new SwitchableEmbedder().Generator;
        (await ServiceAt("ledger", before, store, embedder).ResolveAsync("ledger")).ShouldNotBeNull();
        await RefuseToRecordAPathAsync(connectionString);
        var moved = ServiceAt("ledger", after, store, embedder);

        (await moved.ResolveAsync("ledger")).ShouldNotBeNull();
        (await moved.ResolveAsync("ledger")).ShouldNotBeNull();

        (await SymbolsAsync(store, "ledger")).ShouldBe(["LedgerAfter"]);
        (await store.GetIndexedPathAsync("ledger", CancellationToken.None)).ShouldBe(before);
        await embedder.Received(3).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_Cancelled_By_Its_Caller_While_The_Adopted_Path_Is_Recorded_Throws_The_Cancellation_And_Logs_Nothing()
    {
        using var cancellation = new CancellationTokenSource();
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("ledger", Arg.Any<CancellationToken>()).Returns(true);
        store.GetIndexedPathAsync("ledger", Arg.Any<CancellationToken>()).Returns((string?)null);
        store.SetIndexedPathAsync("ledger", "/repos/ledger", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });
        var logger = new RecordingLogger();
        var service = new ProjectResolutionService(
            Options.Create(new ProjectsOptions { Items = { new ProjectEntry { Name = "ledger", RepoPath = "/repos/ledger" } } }),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            RealIndexer(store),
            store,
            logger);

        await Should.ThrowAsync<OperationCanceledException>(() => service.ResolveAsync("ledger", cancellation.Token));

        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_Cancelled_By_Its_Caller_While_Indexing_Throws_The_Cancellation_And_Logs_Nothing()
    {
        var repo = MakeRepoWithClass("cancelled", "Ledger");
        using var cancellation = new CancellationTokenSource();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<GeneratedEmbeddings<Embedding<float>>>(cancellation.Token);
            });
        var store = new SqliteProjectKnowledgeStore($"Data Source={Path.Combine(_root, "cancelled.db")}");
        var logger = new RecordingLogger();
        var service = ServiceAt("ledger", repo, store, embedder, logger);

        await Should.ThrowAsync<OperationCanceledException>(() => service.ResolveAsync("ledger", cancellation.Token));

        logger.Entries.ShouldBeEmpty();
    }
}
