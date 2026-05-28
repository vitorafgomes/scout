using Microsoft.Extensions.Options;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Embedding;
using Scout.Plugin.CodeQA.Retrieval;
using Scout.Plugin.CodeQA.Storage;
using Scout.Plugin.CodeQA.Tests.Embedding;

namespace Scout.Plugin.CodeQA.Tests.Retrieval;

public class TopKRetrieverTests
{
    private const int Dim = 8;

    private static async Task<(InMemoryVectorStore store, FakeEmbedder embedder)> SeededStoreAsync(
        params (string id, string path, string content)[] items)
    {
        var store = new InMemoryVectorStore();
        var embedder = new FakeEmbedder(Dim);
        var texts = items.Select(i => i.content).ToList();
        var vectors = await embedder.EmbedAsync(texts, CancellationToken.None);

        var chunks = new List<CodeChunk>();
        for (var i = 0; i < items.Length; i++)
        {
            chunks.Add(new CodeChunk(
                items[i].id,
                items[i].path,
                StartLine: 1,
                EndLine: 10,
                Content: items[i].content,
                Embedding: vectors[i]));
        }
        await store.UpsertAsync(chunks, CancellationToken.None);
        return (store, embedder);
    }

    private static IOptions<CodeQAOptions> OptionsWith(int topK)
    {
        var opts = new CodeQAOptions();
        opts.Retrieval.TopK = topK;
        return Options.Create(opts);
    }

    [Fact]
    public void Ctor_Throws_On_Null_Options()
    {
        Should.Throw<ArgumentNullException>(() =>
            new TopKRetriever(null!, new FakeEmbedder(Dim), new InMemoryVectorStore()));
    }

    [Fact]
    public void Ctor_Throws_On_Null_Embedder()
    {
        Should.Throw<ArgumentNullException>(() =>
            new TopKRetriever(OptionsWith(3), null!, new InMemoryVectorStore()));
    }

    [Fact]
    public void Ctor_Throws_On_Null_Store()
    {
        Should.Throw<ArgumentNullException>(() =>
            new TopKRetriever(OptionsWith(3), new FakeEmbedder(Dim), null!));
    }

    [Fact]
    public async Task Retrieve_Throws_On_Empty_Query()
    {
        var (store, embedder) = await SeededStoreAsync(("a", "a.cs", "alpha"));
        var retriever = new TopKRetriever(OptionsWith(3), embedder, store);

        await Should.ThrowAsync<ArgumentException>(() =>
            retriever.RetrieveAsync("  ", null, CancellationToken.None));
    }

    [Fact]
    public async Task Retrieve_Throws_On_Non_Positive_TopK_Override()
    {
        var (store, embedder) = await SeededStoreAsync(("a", "a.cs", "alpha"));
        var retriever = new TopKRetriever(OptionsWith(3), embedder, store);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync("query", topKOverride: 0, CancellationToken.None));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync("query", topKOverride: -1, CancellationToken.None));
    }

    [Fact]
    public async Task Retrieve_Uses_Configured_TopK_When_Override_Is_Null()
    {
        var (store, embedder) = await SeededStoreAsync(
            ("a", "a.cs", "alpha one"),
            ("b", "b.cs", "beta two"),
            ("c", "c.cs", "gamma three"),
            ("d", "d.cs", "delta four"));
        var retriever = new TopKRetriever(OptionsWith(topK: 2), embedder, store);

        var hits = await retriever.RetrieveAsync("alpha one", null, CancellationToken.None);

        hits.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Retrieve_Honours_TopK_Override()
    {
        var (store, embedder) = await SeededStoreAsync(
            ("a", "a.cs", "alpha"),
            ("b", "b.cs", "beta"),
            ("c", "c.cs", "gamma"));
        var retriever = new TopKRetriever(OptionsWith(topK: 10), embedder, store);

        var hits = await retriever.RetrieveAsync("alpha", topKOverride: 1, CancellationToken.None);

        hits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Retrieve_Returns_Empty_When_Store_Is_Empty()
    {
        var store = new InMemoryVectorStore();
        var retriever = new TopKRetriever(OptionsWith(3), new FakeEmbedder(Dim), store);

        var hits = await retriever.RetrieveAsync("anything", null, CancellationToken.None);

        hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Retrieve_Ranks_Best_Match_First()
    {
        var (store, embedder) = await SeededStoreAsync(
            ("a", "a.cs", "alpha alpha alpha"),
            ("b", "b.cs", "beta beta beta"));
        var retriever = new TopKRetriever(OptionsWith(2), embedder, store);

        var hits = await retriever.RetrieveAsync("alpha alpha alpha", null, CancellationToken.None);

        hits[0].Chunk.Id.ShouldBe("a");
    }

    [Fact]
    public async Task Retrieve_Propagates_Embedder_Exception()
    {
        var embedder = Substitute.For<IEmbedder>();
        embedder
            .EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ReadOnlyMemory<float>>>>(
                _ => throw new InvalidOperationException("embed failed"));
        var retriever = new TopKRetriever(OptionsWith(3), embedder, new InMemoryVectorStore());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            retriever.RetrieveAsync("query", null, CancellationToken.None));

        ex.Message.ShouldBe("embed failed");
    }

    [Fact]
    public async Task Retrieve_Propagates_Store_Exception()
    {
        var embedder = new FakeEmbedder(Dim);
        var store = Substitute.For<IVectorStore>();
        store
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<VectorSearchResult>>>(
                _ => throw new InvalidOperationException("search failed"));
        var retriever = new TopKRetriever(OptionsWith(3), embedder, store);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            retriever.RetrieveAsync("query", null, CancellationToken.None));

        ex.Message.ShouldBe("search failed");
    }

    [Fact]
    public async Task Retrieve_Respects_Cancellation()
    {
        var (store, embedder) = await SeededStoreAsync(("a", "a.cs", "alpha"));
        var retriever = new TopKRetriever(OptionsWith(3), embedder, store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            retriever.RetrieveAsync("query", null, cts.Token));
    }
}
