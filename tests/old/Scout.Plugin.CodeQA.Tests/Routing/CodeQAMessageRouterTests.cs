using Scout.Core.Chat;
using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Functions;
using Scout.Plugin.CodeQA.Retrieval;
using Scout.Plugin.CodeQA.Routing;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Tests.Routing;

public class CodeQAMessageRouterTests
{
    private static VectorSearchResult MakeHit(string path = "a.cs", int start = 1, int end = 2)
    {
        var embedding = new ReadOnlyMemory<float>(new[] { 0.1f });
        var chunk = new CodeChunk("id-" + path, path, start, end, "x", embedding);
        return new VectorSearchResult(chunk, 0.7);
    }

    // Builds a real CodeQAFunctions backed by stub retriever + composer so the
    // router exercises the same StartStreamingAskAsync path the production
    // wiring uses.
    private static CodeQAFunctions FunctionsStreaming(
        string answerText,
        VectorSearchResult[]? hits = null)
    {
        var retriever = Substitute.For<IRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>(
                hits ?? Array.Empty<VectorSearchResult>()));

        var composer = Substitute.For<IComposer>();
        composer
            .ComposeStreamingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<VectorSearchResult>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => YieldOne(answerText));

        return new CodeQAFunctions(retriever, composer);
    }

    private static async IAsyncEnumerable<string> YieldOne(string text)
    {
        await Task.Yield();
        yield return text;
    }

    private static async Task<string> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var parts = new List<string>();
        await foreach (var chunk in stream)
        {
            parts.Add(chunk);
        }
        return string.Concat(parts);
    }

    [Theory]
    [InlineData("show me the code")]
    [InlineData("how does this work?")]
    [InlineData("where is the router?")]
    [InlineData("find the config")]
    [InlineData("explain the composer")]
    [InlineData("CODE please")]
    public async Task Matches_Expected_Keywords(string text)
    {
        var functions = FunctionsStreaming("answer");
        var router = new CodeQAMessageRouter(functions);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", text),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.IsStreaming.ShouldBeTrue();
        response.Stream.ShouldNotBeNull();
        (await CollectAsync(response.Stream!)).ShouldContain("answer");
    }

    [Theory]
    [InlineData("good morning team")]
    [InlineData("however you like")] // must not match "how" as substring
    [InlineData("wherewithal")]       // must not match "where" as substring
    [InlineData("explaining")]        // must not match "explain" as substring
    public async Task Returns_Null_For_Non_Matching_Text(string text)
    {
        var functions = FunctionsStreaming("answer");
        var router = new CodeQAMessageRouter(functions);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", text),
            CancellationToken.None);

        response.ShouldBeNull();
    }

    [Fact]
    public async Task Null_Message_Throws()
    {
        var functions = FunctionsStreaming("answer");
        var router = new CodeQAMessageRouter(functions);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            router.RouteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void Ctor_Throws_On_Null_Functions()
    {
        Should.Throw<ArgumentNullException>(() =>
            new CodeQAMessageRouter(null!));
    }

    [Fact]
    public async Task Exposes_Citations_For_Each_Retrieved_Hit()
    {
        var hits = new[]
        {
            MakeHit("a.cs", 1, 2),
            MakeHit("b.cs", 10, 12),
        };
        var functions = FunctionsStreaming("the answer", hits);
        var router = new CodeQAMessageRouter(functions);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "explain code"),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.IsStreaming.ShouldBeTrue();
        response.Citations.Count.ShouldBe(2);
        response.Citations[0].Label.ShouldBe("a.cs:1-2");
        response.Citations[0].Path.ShouldBe("a.cs");
        response.Citations[0].Line.ShouldBe(1);
        response.Citations[1].Label.ShouldBe("b.cs:10-12");
        response.Citations[1].Path.ShouldBe("b.cs");
        response.Citations[1].Line.ShouldBe(10);

        (await CollectAsync(response.Stream!)).ShouldBe("the answer");
    }

    [Fact]
    public async Task Empty_Citations_When_No_Hits()
    {
        var functions = FunctionsStreaming("plain answer");
        var router = new CodeQAMessageRouter(functions);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "explain code"),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.IsStreaming.ShouldBeTrue();
        response.Citations.ShouldBeEmpty();
        (await CollectAsync(response.Stream!)).ShouldBe("plain answer");
    }

    [Fact]
    public async Task Respects_Custom_Keyword_Set()
    {
        var functions = FunctionsStreaming("answer");
        var router = new CodeQAMessageRouter(functions, keywords: new[] { "widget" });

        var matches = await router.RouteAsync(
            new ChatMessage("u1", "c1", "show me the widget"),
            CancellationToken.None);
        var noMatch = await router.RouteAsync(
            new ChatMessage("u1", "c1", "show me the code"),
            CancellationToken.None);

        matches.ShouldNotBeNull();
        noMatch.ShouldBeNull();
    }

    [Fact]
    public async Task Null_Keywords_Falls_Back_To_Defaults()
    {
        var functions = FunctionsStreaming("answer");
        var router = new CodeQAMessageRouter(functions, keywords: null);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "find the thing"),
            CancellationToken.None);

        response.ShouldNotBeNull();
    }
}
