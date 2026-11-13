namespace Scout.Knowledge.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class ProjectKnowledgeServiceTests : IDisposable
{
    private static readonly float[] PairVector = [.. Enumerable.Repeat(0.1f, ChunkRecord.EmbeddingDimensions)];
    private static readonly float[] ZeroVector = [0f];

    private readonly string _repo;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"scout-knowledge-service-{Guid.NewGuid():N}.db");

    public ProjectKnowledgeServiceTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "scout-knowledge-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repo))
        {
            Directory.Delete(_repo, recursive: true);
        }

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // GenerateVectorAsync is an extension method that delegates to GenerateAsync,
    // so the stub targets GenerateAsync (the only virtual member).
    private static IEmbeddingGenerator<string, Embedding<float>> StubEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var generated = new GeneratedEmbeddings<Embedding<float>>
        {
            new Embedding<float>(PairVector),
        };
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(generated);
        return embedder;
    }

    // Resolution that knows "proj" pointing at an existing dir; HasProjectAsync is
    // stubbed true so ResolveAsync returns the entry without lazy indexing, keeping
    // the search/find assertions focused on formatting.
    private ProjectResolutionService Resolution(IProjectKnowledgeStore store, string project = "proj")
    {
        store.HasProjectAsync(project, Arg.Any<CancellationToken>()).Returns(true);
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = project, RepoPath = _repo } },
        };
        var indexer = new ProjectIndexer(
            Substitute.For<IChunker>(),
            Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            store,
            NullLogger<ProjectIndexer>.Instance);
        return new ProjectResolutionService(
            Options.Create(options),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            indexer,
            store,
            NullLogger<ProjectResolutionService>.Instance);
    }

    private ProjectKnowledgeService NewService(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IProjectKnowledgeStore store,
        string project = "proj")
        => new(embedder, store, Resolution(store, project), NullLogger<ProjectKnowledgeService>.Instance);

    private static ChunkRecord Hit(string path, string symbol, int start, int end, string content) => new()
    {
        Id = $"proj:{path}:{start}",
        Project = "proj",
        Path = path,
        Kind = "code",
        Symbol = symbol,
        StartLine = start,
        EndLine = end,
        Content = content,
        Embedding = new ReadOnlyMemory<float>(ZeroVector),
    };

    [Fact]
    public async Task SearchAsync_returns_cited_context_for_each_hit()
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();
        store
            .SearchAsync("proj", Arg.Any<ReadOnlyMemory<float>>(), 5, Arg.Any<CancellationToken>())
            .Returns(new List<ChunkRecord>
            {
                Hit("a.cs", "ClassA", 1, 5, "class A {}"),
                Hit("b.cs", string.Empty, 10, 12, "var x = 1;"),
            });

        var service = NewService(embedder, store);

        var result = await service.SearchAsync("proj", "where is A?", CancellationToken.None);

        result.ShouldContain("[a.cs:1-5 ClassA]");
        result.ShouldContain("class A {}");
        result.ShouldContain("[b.cs:10-12]");
        result.ShouldContain("var x = 1;");
    }

    [Fact]
    public async Task SearchAsync_returns_run_index_message_when_no_results()
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();
        store
            .SearchAsync("proj", Arg.Any<ReadOnlyMemory<float>>(), 5, Arg.Any<CancellationToken>())
            .Returns(new List<ChunkRecord>());

        var service = NewService(embedder, store);

        var result = await service.SearchAsync("proj", "anything", CancellationToken.None);

        result.ShouldBe("No indexed content found for project 'proj'. Run: index proj");
    }

    [Fact]
    public async Task SearchAsync_returns_unknown_project_message_when_not_resolvable()
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();

        // Resolution only knows "proj"; an unconfigured name resolves to null.
        var service = NewService(embedder, store);

        var result = await service.SearchAsync("ghost", "anything", CancellationToken.None);

        result.ShouldBe("Unknown project 'ghost'.");
    }

    [Theory]
    [InlineData(null, "q")]
    [InlineData("  ", "q")]
    [InlineData("proj", null)]
    [InlineData("proj", "  ")]
    public async Task SearchAsync_returns_guard_message_without_calling_dependencies(string? project, string? question)
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();

        var service = NewService(embedder, store);

        var result = await service.SearchAsync(project!, question!, CancellationToken.None);

        result.ShouldBe("Provide a project and a question.");
        await store.DidNotReceive().SearchAsync(
            Arg.Any<string>(),
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_returns_clean_error_when_store_throws()
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();
        store
            .SearchAsync("proj", Arg.Any<ReadOnlyMemory<float>>(), 5, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChunkRecord>>(_ => throw new InvalidOperationException("boom"));

        var service = NewService(embedder, store);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await service.SearchAsync("proj", "anything", CancellationToken.None));

        result.ShouldBe("Could not search project 'proj' right now.");
    }

    [Fact]
    public async Task SearchAsync_returns_clean_error_when_embedder_throws()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<GeneratedEmbeddings<Embedding<float>>>(_ => throw new InvalidOperationException("boom"));
        var store = Substitute.For<IProjectKnowledgeStore>();

        var service = NewService(embedder, store);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await service.SearchAsync("proj", "anything", CancellationToken.None));

        result.ShouldBe("Could not search project 'proj' right now.");
    }

    private static IEmbeddingGenerator<string, Embedding<float>> NoEmbeddingsEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<GeneratedEmbeddings<Embedding<float>>>(_ => throw new NotSupportedException("Embeddings are not available."));
        return embedder;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> WorkingEmbedder()
    {
        var vector = new float[768];
        vector[0] = 1f;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new GeneratedEmbeddings<Embedding<float>> { new Embedding<float>(vector) });
        return embedder;
    }

    private ProjectKnowledgeService ServiceWithoutEmbeddingsOverTheRepo(RecordingLogger<ProjectKnowledgeService> logger)
        => ServiceOverTheRepo(NoEmbeddingsEmbedder(), logger);

    private ProjectKnowledgeService ServiceOverTheRepo(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        RecordingLogger<ProjectKnowledgeService> logger)
    {
        File.WriteAllText(
            Path.Combine(_repo, "Billing.cs"),
            "public class InvoiceCalculator\n{\n    public decimal ApplyDiscount(decimal total) => total * 0.9m;\n}\n");
        File.WriteAllText(Path.Combine(_repo, "billing.md"), "The discount is applied to the invoice total before tax.");
        File.WriteAllText(Path.Combine(_repo, "shipping.md"), "Shipping labels are printed by the warehouse.");
        var store = new SqliteProjectKnowledgeStore($"Data Source={_dbPath}");
        var resolution = new ProjectResolutionService(
            Options.Create(new ProjectsOptions { Items = { new ProjectEntry { Name = "proj", RepoPath = _repo } } }),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            new ProjectIndexer(new FileChunker(), embedder, store, NullLogger<ProjectIndexer>.Instance),
            store,
            NullLogger<ProjectResolutionService>.Instance);
        return new ProjectKnowledgeService(embedder, store, resolution, logger);
    }

    [Fact]
    public async Task SearchAsync_WithoutEmbeddings_AnswersTheTextHitsWithCitations()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceWithoutEmbeddingsOverTheRepo(logger);

        var result = await service.SearchAsync("proj", "Where is the discount applied to the invoice total?", CancellationToken.None);

        result.ShouldStartWith("[billing.md:1-1]\nThe discount is applied to the invoice total before tax.\n\n");
        logger.Exceptions.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithoutEmbeddings_AndNoMatch_SaysNothingMatchesWithoutAskingToReindex()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceWithoutEmbeddingsOverTheRepo(logger);

        var result = await service.SearchAsync("proj", "kubernetes helm chart", CancellationToken.None);

        result.ShouldBe("No content in project 'proj' matches the question.");
        logger.Exceptions.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithEmbeddings_OnAProjectIndexedWithoutVectors_AnswersTheTextHits()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        await ServiceWithoutEmbeddingsOverTheRepo(logger).FindSymbolAsync("proj", "InvoiceCalculator", kind: null, CancellationToken.None);
        var service = ServiceOverTheRepo(WorkingEmbedder(), logger);

        var result = await service.SearchAsync("proj", "Where is the discount applied to the invoice total?", CancellationToken.None);

        result.ShouldStartWith("[billing.md:1-1]\nThe discount is applied to the invoice total before tax.\n\n");
        logger.Exceptions.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_With_An_Embedder_Of_Another_Size_Indexes_The_Text_And_Answers_From_It()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new GeneratedEmbeddings<Embedding<float>>(
                ((IEnumerable<string>)ci[0]).Select(_ => new Embedding<float>(new float[1536]))));
        var service = ServiceOverTheRepo(embedder, logger);

        var result = await service.SearchAsync("proj", "Where is the discount applied to the invoice total?", CancellationToken.None);

        result.ShouldStartWith("[billing.md:1-1]\nThe discount is applied to the invoice total before tax.\n\n");
        logger.Exceptions.ShouldBeEmpty();
    }

    private ProjectKnowledgeService ServiceOverAnUnopenableDatabase(RecordingLogger<ProjectKnowledgeService> logger)
    {
        var unopenable = new SqliteProjectKnowledgeStore($"Data Source={_repo}");
        var embedder = NoEmbeddingsEmbedder();
        var resolution = new ProjectResolutionService(
            Options.Create(new ProjectsOptions { Items = { new ProjectEntry { Name = "proj", RepoPath = _repo } } }),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            new ProjectIndexer(new FileChunker(), embedder, unopenable, NullLogger<ProjectIndexer>.Instance),
            unopenable,
            NullLogger<ProjectResolutionService>.Instance);
        return new ProjectKnowledgeService(embedder, unopenable, resolution, logger);
    }

    [Fact]
    public async Task SearchAsync_WhenTheDatabaseCannotOpen_AnswersCleanlyAndWarnsOnce()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceOverAnUnopenableDatabase(logger);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await service.SearchAsync("proj", "Where is the invoice total?", CancellationToken.None));

        result.ShouldBe("Could not search project 'proj' right now.");
        logger.Levels.ShouldBe([LogLevel.Warning]);
        logger.Exceptions.ShouldHaveSingleItem().ShouldBeOfType<SqliteException>();
    }

    [Fact]
    public async Task FindSymbolAsync_WhenTheDatabaseCannotOpen_AnswersCleanlyAndWarnsOnce()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceOverAnUnopenableDatabase(logger);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await service.FindSymbolAsync("proj", "InvoiceCalculator", kind: null, CancellationToken.None));

        result.ShouldBe("Could not find symbol 'InvoiceCalculator' in project 'proj' right now.");
        logger.Levels.ShouldBe([LogLevel.Warning]);
        logger.Exceptions.ShouldHaveSingleItem().ShouldBeOfType<SqliteException>();
    }

    private static IEmbeddingGenerator<string, Embedding<float>> UnreachableEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new HttpRequestException("Connection refused")));
        return embedder;
    }

    [Fact]
    public async Task SearchAsync_When_Indexing_Fails_And_There_Is_No_Index_Says_That_Indexing_Failed_Not_Unknown_Project()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceOverTheRepo(UnreachableEmbedder(), logger);

        var result = await service.SearchAsync("proj", "Where is the discount applied?", CancellationToken.None);

        result.ShouldBe("Indexing project 'proj' failed, so it cannot be searched yet. Try again later.");
        logger.Levels.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindSymbolAsync_When_Indexing_Fails_And_There_Is_No_Index_Says_That_Indexing_Failed_Not_Unknown_Project()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceOverTheRepo(UnreachableEmbedder(), logger);

        var result = await service.FindSymbolAsync("proj", "InvoiceCalculator", kind: null, CancellationToken.None);

        result.ShouldBe("Indexing project 'proj' failed, so it cannot be searched yet. Try again later.");
        logger.Levels.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindSymbolAsync_When_The_Recorded_Path_Cannot_Be_Read_Answers_Cleanly_Warns_Once_And_Indexes_Nothing()
    {
        var embedder = NoEmbeddingsEmbedder();
        (await ServiceOverTheRepo(embedder, new RecordingLogger<ProjectKnowledgeService>())
            .FindSymbolAsync("proj", "InvoiceCalculator", kind: null, CancellationToken.None)).ShouldStartWith("[Billing.cs:1-4 InvoiceCalculator]");
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE project_source; CREATE TABLE project_source (project TEXT PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        embedder.ClearReceivedCalls();
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceOverTheRepo(embedder, logger);

        var result = await service.FindSymbolAsync("proj", "InvoiceCalculator", kind: null, CancellationToken.None);

        result.ShouldBe("Could not find symbol 'InvoiceCalculator' in project 'proj' right now.");
        logger.Levels.ShouldBe([LogLevel.Warning]);
        logger.Exceptions.ShouldHaveSingleItem().ShouldBeOfType<SqliteException>().Message.ShouldContain("repo_path");
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        (await new SqliteProjectKnowledgeStore($"Data Source={_dbPath}").FindSymbolAsync("proj", "InvoiceCalculator", null, 5, CancellationToken.None))
            .ShouldHaveSingleItem().Path.ShouldBe("Billing.cs");
    }

    [Fact]
    public async Task SearchAsync_When_Cancelled_Throws_The_Cancellation_Instead_Of_Answering_That_It_Could_Not_Search()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceWithoutEmbeddingsOverTheRepo(logger);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            service.SearchAsync("proj", "Where is the discount applied?", cancelled.Token));

        logger.Levels.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindSymbolAsync_When_Cancelled_Throws_The_Cancellation_Instead_Of_Answering_That_It_Could_Not_Find_The_Symbol()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceWithoutEmbeddingsOverTheRepo(logger);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            service.FindSymbolAsync("proj", "InvoiceCalculator", kind: null, cancelled.Token));

        logger.Levels.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindSymbolAsync_WithoutEmbeddings_AnswersTheSymbolHits()
    {
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceWithoutEmbeddingsOverTheRepo(logger);

        var result = await service.FindSymbolAsync("proj", "InvoiceCalculator", kind: null, CancellationToken.None);

        result.ShouldStartWith("[Billing.cs:1-4 InvoiceCalculator]\npublic class InvoiceCalculator");
        logger.Exceptions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Money", "[Kinds.cs:3-3 Money]\npublic struct Money { }")]
    [InlineData("Colour", "[Kinds.cs:4-4 Colour]\npublic enum Colour { Red }")]
    [InlineData("Combine", "[Kinds.cs:5-5 Combine]\npublic delegate int Combine(int left, int right);")]
    [InlineData("_count", "[Kinds.cs:8-8 _count]\nprivate readonly int _count = 1;")]
    [InlineData("Basket", "[Kinds.cs:9-9 Basket]\npublic Basket(int count) { _count = count; }")]
    [InlineData("Total", "[Kinds.cs:10-10 Total]\npublic int Total { get; set; }")]
    [InlineData("Emptied", "[Kinds.cs:11-11 Emptied]\npublic event System.EventHandler? Emptied;")]
    [InlineData("Filled", "[Kinds.cs:12-12 Filled]\npublic event System.EventHandler Filled { add { } remove { } }")]
    public async Task FindSymbolAsync_Finds_Every_Member_Kind_By_Its_Name(string symbol, string expected)
    {
        File.WriteAllText(
            Path.Combine(_repo, "Kinds.cs"),
            """
            namespace Shop;

            public struct Money { }
            public enum Colour { Red }
            public delegate int Combine(int left, int right);
            public class Basket
            {
                private readonly int _count = 1;
                public Basket(int count) { _count = count; }
                public int Total { get; set; }
                public event System.EventHandler? Emptied;
                public event System.EventHandler Filled { add { } remove { } }
            }
            """);
        var service = ServiceWithoutEmbeddingsOverTheRepo(new RecordingLogger<ProjectKnowledgeService>());

        var result = await service.FindSymbolAsync("proj", symbol, kind: null, CancellationToken.None);

        result.Split("\n\n").ShouldContain(expected);
    }

    [Fact]
    public async Task SearchAsync_By_Vector_Returns_No_Field_Chunk()
    {
        File.WriteAllText(
            Path.Combine(_repo, "Basket.cs"),
            "public class Basket\n{\n    private int _count;\n    private int _limit;\n    private string _owner = \"\";\n    public int Total() => _count;\n}\n");
        var vector = new float[ChunkRecord.EmbeddingDimensions];
        vector[0] = 1f;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => new GeneratedEmbeddings<Embedding<float>>(
                ((IEnumerable<string>)call[0]).Select(_ => new Embedding<float>(vector))));
        var store = new SqliteProjectKnowledgeStore($"Data Source={_dbPath}");
        var resolution = new ProjectResolutionService(
            Options.Create(new ProjectsOptions { Items = { new ProjectEntry { Name = "proj", RepoPath = _repo } } }),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            new ProjectIndexer(new FileChunker(), embedder, store, NullLogger<ProjectIndexer>.Instance),
            store,
            NullLogger<ProjectResolutionService>.Instance);
        var service = new ProjectKnowledgeService(embedder, store, resolution, NullLogger<ProjectKnowledgeService>.Instance);

        var result = await service.SearchAsync("proj", "How many items does the basket hold?", CancellationToken.None);

        result.Split("\n\n").Select(block => block.Split('\n')[0]).ShouldBe(
            ["[Basket.cs:1-7 Basket]", "[Basket.cs:6-6 Total]"], ignoreOrder: true);
        (await service.FindSymbolAsync("proj", "_owner", kind: null, CancellationToken.None))
            .ShouldBe("[Basket.cs:5-5 _owner]\nprivate string _owner = \"\";");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<Exception> Exceptions { get; } = [];

        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
    }

    [Fact]
    public async Task FindSymbolAsync_does_not_call_the_embedder()
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();
        store
            .FindSymbolAsync("proj", "Bar", null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ChunkRecord> { Hit("a.cs", "Bar", 1, 3, "int Bar() => 1;") });

        var service = NewService(embedder, store);

        await service.FindSymbolAsync("proj", "Bar", kind: null, CancellationToken.None);

        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindSymbolAsync_returns_cited_hits_when_store_returns_rows()
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();
        store
            .FindSymbolAsync("proj", "ClassA", null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ChunkRecord>
            {
                Hit("a.cs", "ClassA", 1, 5, "class ClassA {}"),
            });

        var service = NewService(embedder, store);

        var result = await service.FindSymbolAsync("proj", "ClassA", kind: null, CancellationToken.None);

        result.ShouldContain("[a.cs:1-5 ClassA]");
        result.ShouldContain("class ClassA {}");
    }

    [Fact]
    public async Task FindSymbolAsync_returns_run_index_message_when_no_results()
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();
        store
            .FindSymbolAsync("proj", "Ghost", null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ChunkRecord>());

        var service = NewService(embedder, store);

        var result = await service.FindSymbolAsync("proj", "Ghost", kind: null, CancellationToken.None);

        result.ShouldBe("No symbol matching 'Ghost' found in project 'proj'. If the project is not indexed yet, run: index proj.");
    }

    [Theory]
    [InlineData(null, "Sym")]
    [InlineData("  ", "Sym")]
    [InlineData("proj", null)]
    [InlineData("proj", "  ")]
    public async Task FindSymbolAsync_returns_guard_message_without_calling_dependencies(string? project, string? symbol)
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();

        var service = NewService(embedder, store);

        var result = await service.FindSymbolAsync(project!, symbol!, kind: null, CancellationToken.None);

        result.ShouldBe("Provide a project and a symbol.");
        await store.DidNotReceive().FindSymbolAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindSymbolAsync_returns_unknown_project_message_when_not_resolvable()
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();

        var service = NewService(embedder, store);

        var result = await service.FindSymbolAsync("ghost", "Bar", kind: null, CancellationToken.None);

        result.ShouldBe("Unknown project 'ghost'.");
    }

    [Fact]
    public async Task FindSymbolAsync_returns_clean_error_when_store_throws()
    {
        var embedder = StubEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();
        store
            .FindSymbolAsync("proj", "Boom", null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChunkRecord>>(_ => throw new InvalidOperationException("boom"));

        var service = NewService(embedder, store);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await service.FindSymbolAsync("proj", "Boom", kind: null, CancellationToken.None));

        result.ShouldBe("Could not find symbol 'Boom' in project 'proj' right now.");
    }

    private static TaskCanceledException RequestTimeout()
        => new("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());

    [Fact]
    public async Task SearchAsync_When_The_Embedding_Call_Times_Out_And_Nobody_Cancelled_Answers_That_It_Could_Not_Search_And_Warns_Once()
    {
        var timesOut = false;
        var vector = new float[ChunkRecord.EmbeddingDimensions];
        vector[0] = 1f;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => timesOut
                ? Task.FromException<GeneratedEmbeddings<Embedding<float>>>(RequestTimeout())
                : Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                    ((IEnumerable<string>)call[0]).Select(_ => new Embedding<float>(vector)))));
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceOverTheRepo(embedder, logger);
        (await service.FindSymbolAsync("proj", "InvoiceCalculator", kind: null, CancellationToken.None))
            .ShouldStartWith("[Billing.cs:1-4 InvoiceCalculator]");
        timesOut = true;

        var result = await service.SearchAsync("proj", "Where is the discount applied?", CancellationToken.None);

        result.ShouldBe("Could not search project 'proj' right now.");
        logger.Levels.ShouldBe([LogLevel.Warning]);
        logger.Exceptions.ShouldHaveSingleItem().ShouldBeOfType<TaskCanceledException>();
    }

    [Fact]
    public async Task FindSymbolAsync_When_Indexing_Times_Out_And_Nobody_Cancelled_Says_That_Indexing_Failed()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GeneratedEmbeddings<Embedding<float>>>(RequestTimeout()));
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = ServiceOverTheRepo(embedder, logger);

        var result = await service.FindSymbolAsync("proj", "InvoiceCalculator", kind: null, CancellationToken.None);

        result.ShouldBe("Indexing project 'proj' failed, so it cannot be searched yet. Try again later.");
        logger.Levels.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindSymbolAsync_When_The_Store_Call_Is_Cancelled_And_Nobody_Cancelled_Answers_That_It_Could_Not_Find_The_Symbol_And_Warns_Once()
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        store
            .FindSymbolAsync("proj", "Bar", null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ChunkRecord>>(new TaskCanceledException("A task was canceled.")));
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = new ProjectKnowledgeService(StubEmbedder(), store, Resolution(store), logger);

        var result = await service.FindSymbolAsync("proj", "Bar", kind: null, CancellationToken.None);

        result.ShouldBe("Could not find symbol 'Bar' in project 'proj' right now.");
        logger.Levels.ShouldBe([LogLevel.Warning]);
        logger.Exceptions.ShouldHaveSingleItem().ShouldBeOfType<TaskCanceledException>();
    }

    [Fact]
    public async Task FindSymbolAsync_When_Indexing_Fails_And_Then_The_Recorded_Path_Cannot_Be_Read_Answers_Cleanly_And_Warns_Once()
    {
        var down = false;
        var vector = new float[ChunkRecord.EmbeddingDimensions];
        vector[0] = 1f;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => down
                ? Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new HttpRequestException("Connection refused"))
                : Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                    ((IEnumerable<string>)call[0]).Select(_ => new Embedding<float>(vector)))));
        (await ServiceOverTheRepo(embedder, new RecordingLogger<ProjectKnowledgeService>())
            .FindSymbolAsync("proj", "InvoiceCalculator", kind: null, CancellationToken.None)).ShouldStartWith("[Billing.cs:1-4 InvoiceCalculator]");
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE project_source; CREATE TABLE project_source (project TEXT PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteProjectKnowledgeStore($"Data Source={_dbPath}");
        var registeredAgain = new ProjectResolutionService(
            Options.Create(new ProjectsOptions()),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            new ProjectIndexer(new FileChunker(), embedder, store, NullLogger<ProjectIndexer>.Instance),
            store,
            NullLogger<ProjectResolutionService>.Instance);
        registeredAgain.Register(new ProjectEntry { Name = "proj", RepoPath = _repo });
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = new ProjectKnowledgeService(embedder, store, registeredAgain, logger);
        down = true;

        var result = await service.FindSymbolAsync("proj", "InvoiceCalculator", kind: null, CancellationToken.None);

        result.ShouldBe("Could not find symbol 'InvoiceCalculator' in project 'proj' right now.");
        logger.Levels.ShouldBe([LogLevel.Warning]);
        logger.Exceptions.ShouldHaveSingleItem().ShouldBeOfType<SqliteException>().Message.ShouldContain("repo_path");
    }
}
