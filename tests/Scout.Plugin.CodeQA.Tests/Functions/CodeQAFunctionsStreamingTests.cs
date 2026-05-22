using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Functions;
using Scout.Plugin.CodeQA.Retrieval;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Tests.Functions;

public class CodeQAFunctionsStreamingTests
{
    private static VectorSearchResult MakeHit(
        string path = "a.cs",
        int start = 1,
        int end = 5,
        string content = "line1",
        double score = 0.5)
    {
        var embedding = new ReadOnlyMemory<float>(new[] { 0.1f });
        var chunk = new CodeChunk("id-" + path + "-" + start, path, start, end, content, embedding);
        return new VectorSearchResult(chunk, score);
    }

    private static IRetriever RetrieverReturning(params VectorSearchResult[] hits)
    {
        var retriever = Substitute.For<IRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>(hits));
        return retriever;
    }

    private static async IAsyncEnumerable<string> StaticStream(params string[] tokens)
    {
        foreach (var t in tokens)
        {
            await Task.Yield();
            yield return t;
        }
    }

    [Fact]
    public async Task StartStreamingAskAsync_ReturnsCitationsFromRetrieval()
    {
        var hits = new[]
        {
            MakeHit("a.cs", 1, 5),
            MakeHit("b.cs", 10, 20),
        };
        var retriever = RetrieverReturning(hits);
        var composer = Substitute.For<IComposer>();
        composer
            .ComposeStreamingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<VectorSearchResult>>(),
                Arg.Any<CancellationToken>())
            .Returns(StaticStream("ok"));
        var functions = new CodeQAFunctions(retriever, composer);

        var result = await functions.StartStreamingAskAsync("q", CancellationToken.None);

        result.Citations.Count.ShouldBe(2);
        result.Citations[0].Path.ShouldBe("a.cs");
        result.Citations[0].Line.ShouldBe(1);
        result.Citations[0].Label.ShouldBe("a.cs:1-5");
        result.Citations[1].Path.ShouldBe("b.cs");
        result.Citations[1].Line.ShouldBe(10);
    }

    [Fact]
    public async Task StartStreamingAskAsync_ReturnsStreamFromComposer()
    {
        var hits = new[] { MakeHit("a.cs") };
        var retriever = RetrieverReturning(hits);
        var composer = Substitute.For<IComposer>();
        composer
            .ComposeStreamingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<VectorSearchResult>>(),
                Arg.Any<CancellationToken>())
            .Returns(StaticStream("Hel", "lo"));
        var functions = new CodeQAFunctions(retriever, composer);

        var result = await functions.StartStreamingAskAsync("q", CancellationToken.None);

        // Retrieval ran first (already awaited); composer call wired to stream.
        await retriever.Received(1).RetrieveAsync("q", null, Arg.Any<CancellationToken>());
        composer.Received(1).ComposeStreamingAsync(
            "q", hits, Arg.Any<CancellationToken>());

        var collected = new List<string>();
        await foreach (var token in result.Tokens)
        {
            collected.Add(token);
        }
        collected.ShouldBe(new[] { "Hel", "lo" });
    }

    [Fact]
    public async Task StartStreamingAskAsync_NullQuestion_Throws()
    {
        var functions = new CodeQAFunctions(
            Substitute.For<IRetriever>(),
            Substitute.For<IComposer>());

        await Should.ThrowAsync<ArgumentException>(() =>
            functions.StartStreamingAskAsync("   ", CancellationToken.None));
    }
}
