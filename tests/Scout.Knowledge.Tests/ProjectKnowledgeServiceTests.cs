namespace Scout.Knowledge.Tests;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class ProjectKnowledgeServiceTests : IDisposable
{
    private readonly string _repo;

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
    }

    // GenerateVectorAsync is an extension method that delegates to GenerateAsync,
    // so the stub targets GenerateAsync (the only virtual member).
    private static IEmbeddingGenerator<string, Embedding<float>> StubEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var generated = new GeneratedEmbeddings<Embedding<float>>
        {
            new Embedding<float>(new float[] { 0.1f, 0.2f }),
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
        Embedding = new ReadOnlyMemory<float>(new float[] { 0f }),
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

    private const string NoEmbeddingsMessage = "Project search is not available with the current AI provider.";

    private static IEmbeddingGenerator<string, Embedding<float>> NoEmbeddingsEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<GeneratedEmbeddings<Embedding<float>>>(_ => throw new NotSupportedException("Embeddings are not available."));
        return embedder;
    }

    [Fact]
    public async Task SearchAsync_says_project_search_is_unavailable_when_the_provider_has_no_embeddings()
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        var logger = new RecordingLogger<ProjectKnowledgeService>();
        var service = new ProjectKnowledgeService(NoEmbeddingsEmbedder(), store, Resolution(store), logger);

        var result = await service.SearchAsync("proj", "anything", CancellationToken.None);

        result.ShouldBe(NoEmbeddingsMessage);
        logger.Exceptions.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_on_an_unindexed_project_without_embeddings_stops_indexing_and_says_search_is_unavailable()
    {
        File.WriteAllText(Path.Combine(_repo, "a.md"), "first file");
        File.WriteAllText(Path.Combine(_repo, "b.md"), "second file");
        var embedder = NoEmbeddingsEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("proj", Arg.Any<CancellationToken>()).Returns(false);
        var indexerLogger = new RecordingLogger<ProjectIndexer>();
        var resolutionLogger = new RecordingLogger<ProjectResolutionService>();
        var serviceLogger = new RecordingLogger<ProjectKnowledgeService>();
        var resolution = new ProjectResolutionService(
            Options.Create(new ProjectsOptions { Items = { new ProjectEntry { Name = "proj", RepoPath = _repo } } }),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            new ProjectIndexer(new FileChunker(), embedder, store, indexerLogger),
            store,
            resolutionLogger);
        var service = new ProjectKnowledgeService(embedder, store, resolution, serviceLogger);

        var result = await service.SearchAsync("proj", "anything", CancellationToken.None);

        result.ShouldBe(NoEmbeddingsMessage);
        await embedder.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ReplaceProjectAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<ChunkRecord>>(), Arg.Any<CancellationToken>());
        indexerLogger.Exceptions.ShouldBeEmpty();
        resolutionLogger.Exceptions.ShouldBeEmpty();
        serviceLogger.Exceptions.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindSymbolAsync_on_an_unindexed_project_without_embeddings_says_search_is_unavailable()
    {
        File.WriteAllText(Path.Combine(_repo, "a.md"), "first file");
        var embedder = NoEmbeddingsEmbedder();
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("proj", Arg.Any<CancellationToken>()).Returns(false);
        var resolution = new ProjectResolutionService(
            Options.Create(new ProjectsOptions { Items = { new ProjectEntry { Name = "proj", RepoPath = _repo } } }),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            new ProjectIndexer(new FileChunker(), embedder, store, NullLogger<ProjectIndexer>.Instance),
            store,
            NullLogger<ProjectResolutionService>.Instance);
        var service = new ProjectKnowledgeService(embedder, store, resolution, NullLogger<ProjectKnowledgeService>.Instance);

        var result = await service.FindSymbolAsync("proj", "Anything", kind: null, CancellationToken.None);

        result.ShouldBe(NoEmbeddingsMessage);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<Exception> Exceptions { get; } = [];

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
}
