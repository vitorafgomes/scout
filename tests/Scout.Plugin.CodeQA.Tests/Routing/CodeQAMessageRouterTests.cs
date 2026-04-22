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

    private static CodeQAFunctions FunctionsReturning(
        CodeAnswer answer,
        VectorSearchResult[]? hits = null)
    {
        var retriever = Substitute.For<IRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>(
                hits ?? Array.Empty<VectorSearchResult>()));

        var composer = Substitute.For<IComposer>();
        composer
            .ComposeAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<VectorSearchResult>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(answer));

        return new CodeQAFunctions(retriever, composer);
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
        var functions = FunctionsReturning(
            new CodeAnswer("answer", Array.Empty<Citation>(), "m", TimeSpan.Zero));
        var router = new CodeQAMessageRouter(functions);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", text),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Text.ShouldContain("answer");
    }

    [Theory]
    [InlineData("good morning team")]
    [InlineData("however you like")] // must not match "how" as substring
    [InlineData("wherewithal")]       // must not match "where" as substring
    [InlineData("explaining")]        // must not match "explain" as substring
    public async Task Returns_Null_For_Non_Matching_Text(string text)
    {
        var functions = FunctionsReturning(
            new CodeAnswer("answer", Array.Empty<Citation>(), "m", TimeSpan.Zero));
        var router = new CodeQAMessageRouter(functions);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", text),
            CancellationToken.None);

        response.ShouldBeNull();
    }

    [Fact]
    public async Task Null_Message_Throws()
    {
        var functions = FunctionsReturning(
            new CodeAnswer("answer", Array.Empty<Citation>(), "m", TimeSpan.Zero));
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
    public async Task Appends_Sources_Footer_When_Citations_Present()
    {
        var citations = new[]
        {
            new Citation("a.cs", 1, 2, 0.9),
            new Citation("b.cs", 10, 12, 0.5),
        };
        var functions = FunctionsReturning(
            new CodeAnswer("the answer", citations, "m", TimeSpan.Zero));
        var router = new CodeQAMessageRouter(functions);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "explain code"),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Text.ShouldContain("the answer");
        response.Text.ShouldContain("Sources:");
        response.Text.ShouldContain("- a.cs:1-2");
        response.Text.ShouldContain("- b.cs:10-12");
        response.Citations.Count.ShouldBe(2);
        response.Citations[0].ShouldBe("a.cs:1-2");
        response.Citations[1].ShouldBe("b.cs:10-12");
    }

    [Fact]
    public async Task Omits_Sources_Footer_When_No_Citations()
    {
        var functions = FunctionsReturning(
            new CodeAnswer("plain answer", Array.Empty<Citation>(), "m", TimeSpan.Zero));
        var router = new CodeQAMessageRouter(functions);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "explain code"),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Text.ShouldBe("plain answer");
        response.Text.ShouldNotContain("Sources:");
        response.Citations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Respects_Custom_Keyword_Set()
    {
        var functions = FunctionsReturning(
            new CodeAnswer("answer", Array.Empty<Citation>(), "m", TimeSpan.Zero));
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
        var functions = FunctionsReturning(
            new CodeAnswer("answer", Array.Empty<Citation>(), "m", TimeSpan.Zero));
        var router = new CodeQAMessageRouter(functions, keywords: null);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "find the thing"),
            CancellationToken.None);

        response.ShouldNotBeNull();
    }
}
