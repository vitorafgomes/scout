namespace Scout.Knowledge.Tests;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ProjectKnowledgeServiceTests
{
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

        var service = new ProjectKnowledgeService(embedder, store, NullLogger<ProjectKnowledgeService>.Instance);

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

        var service = new ProjectKnowledgeService(embedder, store, NullLogger<ProjectKnowledgeService>.Instance);

        var result = await service.SearchAsync("proj", "anything", CancellationToken.None);

        result.ShouldBe("No indexed content found for project 'proj'. Run: index proj");
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

        var service = new ProjectKnowledgeService(embedder, store, NullLogger<ProjectKnowledgeService>.Instance);

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

        var service = new ProjectKnowledgeService(embedder, store, NullLogger<ProjectKnowledgeService>.Instance);

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

        var service = new ProjectKnowledgeService(embedder, store, NullLogger<ProjectKnowledgeService>.Instance);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await service.SearchAsync("proj", "anything", CancellationToken.None));

        result.ShouldBe("Could not search project 'proj' right now.");
    }
}
