using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Functions;
using Scout.Plugin.CodeQA.Retrieval;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Tests.Functions;

public class CodeQAFunctionsTests
{
    private static VectorSearchResult MakeHit(
        string path = "a.cs",
        int start = 1,
        int end = 5,
        string content = "line1\nline2",
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

    private static IComposer ComposerReturning(CodeAnswer answer)
    {
        var composer = Substitute.For<IComposer>();
        composer
            .ComposeAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<VectorSearchResult>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(answer));
        return composer;
    }

    [Fact]
    public void Ctor_Throws_On_Null_Retriever()
    {
        Should.Throw<ArgumentNullException>(() =>
            new CodeQAFunctions(null!, Substitute.For<IComposer>()));
    }

    [Fact]
    public void Ctor_Throws_On_Null_Composer()
    {
        Should.Throw<ArgumentNullException>(() =>
            new CodeQAFunctions(Substitute.For<IRetriever>(), null!));
    }

    [Fact]
    public async Task AskCode_Uses_Default_TopK_From_Configuration()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>(Array.Empty<VectorSearchResult>()));
        var composer = ComposerReturning(new CodeAnswer(
            "answer", Array.Empty<Citation>(), "m", TimeSpan.Zero));
        var functions = new CodeQAFunctions(retriever, composer);

        await functions.AskCodeAsync("why?", CancellationToken.None);

        await retriever.Received(1).RetrieveAsync("why?", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskCode_Returns_Answer_Text_Only_When_No_Citations()
    {
        var retriever = RetrieverReturning();
        var composer = ComposerReturning(new CodeAnswer(
            "bare answer", Array.Empty<Citation>(), "m", TimeSpan.Zero));
        var functions = new CodeQAFunctions(retriever, composer);

        var text = await functions.AskCodeAsync("q", CancellationToken.None);

        text.ShouldBe("bare answer");
    }

    [Fact]
    public async Task AskCode_Appends_Sources_Footer_When_Citations_Exist()
    {
        var citations = new[]
        {
            new Citation("a.cs", 1, 2, 0.9),
            new Citation("b.cs", 10, 20, 0.8),
        };
        var retriever = RetrieverReturning(MakeHit());
        var composer = ComposerReturning(new CodeAnswer("answer", citations, "m", TimeSpan.Zero));
        var functions = new CodeQAFunctions(retriever, composer);

        var text = await functions.AskCodeAsync("q", CancellationToken.None);

        text.ShouldContain("answer");
        text.ShouldContain("Sources:");
        text.ShouldContain("- a.cs:1-2");
        text.ShouldContain("- b.cs:10-20");
    }

    [Fact]
    public async Task SearchCode_Forwards_TopK_Override()
    {
        var retriever = RetrieverReturning();
        var functions = new CodeQAFunctions(retriever, Substitute.For<IComposer>());

        await functions.SearchCodeAsync("q", topK: 4, CancellationToken.None);

        await retriever.Received(1).RetrieveAsync("q", 4, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCode_Returns_No_Matches_On_Empty_Result()
    {
        var retriever = RetrieverReturning();
        var functions = new CodeQAFunctions(retriever, Substitute.For<IComposer>());

        var text = await functions.SearchCodeAsync("q", null, CancellationToken.None);

        text.ShouldBe("No matches.");
    }

    [Fact]
    public async Task SearchCode_Renders_One_Line_Per_Hit_With_Score()
    {
        var hits = new[]
        {
            MakeHit("a.cs", 1, 3, "hello world\nsecond", 0.912),
            MakeHit("b.cs", 5, 9, "   \nactual first non-blank line", 0.500),
        };
        var retriever = RetrieverReturning(hits);
        var functions = new CodeQAFunctions(retriever, Substitute.For<IComposer>());

        var text = await functions.SearchCodeAsync("q", null, CancellationToken.None);

        text.ShouldContain("- a.cs:1-3 (score=0.912)");
        text.ShouldContain("hello world");
        text.ShouldContain("- b.cs:5-9 (score=0.500)");
        text.ShouldContain("actual first non-blank line");
    }

    [Fact]
    public async Task SearchCode_Truncates_Long_Preview_Line()
    {
        var longLine = new string('x', 300);
        var hits = new[] { MakeHit("a.cs", 1, 2, longLine, 0.5) };
        var retriever = RetrieverReturning(hits);
        var functions = new CodeQAFunctions(retriever, Substitute.For<IComposer>());

        var text = await functions.SearchCodeAsync("q", null, CancellationToken.None);

        // 120 chars of 'x' should be present, but not the whole 300.
        text.ShouldContain(new string('x', 120));
        text.ShouldNotContain(new string('x', 121));
    }

    [Fact]
    public async Task Internal_AskAsync_Passes_Retrieved_Hits_To_Composer()
    {
        var hits = new[] { MakeHit("a.cs") };
        var retriever = RetrieverReturning(hits);
        var composer = Substitute.For<IComposer>();
        composer
            .ComposeAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<VectorSearchResult>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CodeAnswer("ok", Array.Empty<Citation>(), "m", TimeSpan.Zero)));
        var functions = new CodeQAFunctions(retriever, composer);

        var answer = await functions.AskAsync("q", CancellationToken.None);

        answer.Text.ShouldBe("ok");
        await composer.Received(1).ComposeAsync("q", hits, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskCode_Propagates_Cancellation()
    {
        var retriever = RetrieverReturning();
        var composer = ComposerReturning(new CodeAnswer("x", Array.Empty<Citation>(), "m", TimeSpan.Zero));
        var functions = new CodeQAFunctions(retriever, composer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<VectorSearchResult>>>(ci =>
            {
                ci.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<VectorSearchResult>>(Array.Empty<VectorSearchResult>());
            });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            functions.AskCodeAsync("q", cts.Token));
    }
}
