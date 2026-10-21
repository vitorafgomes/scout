using Scout.Plugin.Wikipedia;
using Scout.Plugin.Wikipedia.Abstractions;

namespace Scout.Plugin.Wikipedia.Tests;

public sealed class WikipediaPluginTests
{
    private static IWikipediaClient Client() => Substitute.For<IWikipediaClient>();

    private static WikipediaPlugin Plugin(IWikipediaClient client) => new(client);

    [Fact]
    public async Task Search_Formats_Results()
    {
        var client = Client();
        client.SearchAsync("turing", "en", Arg.Any<CancellationToken>())
            .Returns(new List<WikipediaSearchResult>
            {
                new("Alan Turing", "British mathematician"),
                new("Turing machine", "abstract model of computation"),
            });

        var result = await Plugin(client).SearchWikipediaAsync("turing", "en", CancellationToken.None);

        result.ShouldContain("Alan Turing - British mathematician");
        result.ShouldContain("Turing machine - abstract model of computation");
    }

    [Fact]
    public async Task Search_Empty_Returns_Message()
    {
        var client = Client();
        client.SearchAsync("qwxyz", "en", Arg.Any<CancellationToken>())
            .Returns(new List<WikipediaSearchResult>());

        var result = await Plugin(client).SearchWikipediaAsync("qwxyz", "en", CancellationToken.None);

        result.ShouldBe("No Wikipedia articles found for qwxyz.");
    }

    [Fact]
    public async Task Search_Defaults_Language_To_English()
    {
        var client = Client();
        client.SearchAsync("turing", "en", Arg.Any<CancellationToken>())
            .Returns(new List<WikipediaSearchResult> { new("Alan Turing", "snippet") });

        var result = await Plugin(client).SearchWikipediaAsync("turing");

        result.ShouldContain("Alan Turing");
        await client.Received(1).SearchAsync("turing", "en", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_Normalizes_Uppercase_Language()
    {
        var client = Client();
        client.SearchAsync("gato", "pt", Arg.Any<CancellationToken>())
            .Returns(new List<WikipediaSearchResult> { new("Gato", "animal") });

        await Plugin(client).SearchWikipediaAsync("gato", "PT", CancellationToken.None);

        await client.Received(1).SearchAsync("gato", "pt", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_Blank_Query_Returns_Teaching_Without_Calling_Client()
    {
        var client = Client();

        var result = await Plugin(client).SearchWikipediaAsync("   ", "en", CancellationToken.None);

        result.ShouldContain("Provide a search term");
        await client.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_Invalid_Language_Returns_Teaching_Without_Calling_Client()
    {
        var client = Client();

        var result = await Plugin(client).SearchWikipediaAsync("turing", "en_US!", CancellationToken.None);

        result.ShouldContain("not a valid Wikipedia language code");
        await client.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_HttpRequestException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.SearchAsync("turing", "en", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WikipediaSearchResult>>(
                _ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.SearchWikipediaAsync("turing", "en", CancellationToken.None));

        result.ShouldBe("Could not reach Wikipedia. Try again later.");
    }

    [Fact]
    public async Task GetSummary_Formats_Extract_And_Url()
    {
        var client = Client();
        client.GetSummaryAsync("Alan Turing", "en", Arg.Any<CancellationToken>())
            .Returns(new WikipediaArticleSummary(
                "Alan Turing",
                "Alan Turing was a mathematician.",
                "https://en.wikipedia.org/wiki/Alan_Turing"));

        var result = await Plugin(client)
            .GetArticleSummaryAsync("Alan Turing", "en", CancellationToken.None);

        result.ShouldContain("Alan Turing was a mathematician.");
        result.ShouldContain("URL: https://en.wikipedia.org/wiki/Alan_Turing");
    }

    [Fact]
    public async Task GetSummary_Missing_Returns_NotFound_Sentence()
    {
        var client = Client();
        client.GetSummaryAsync("Ghost Article", "en", Arg.Any<CancellationToken>())
            .Returns((WikipediaArticleSummary?)null);

        var result = await Plugin(client)
            .GetArticleSummaryAsync("Ghost Article", "en", CancellationToken.None);

        result.ShouldBe("No Wikipedia article named Ghost Article.");
    }

    [Fact]
    public async Task GetSummary_Blank_Title_Returns_Teaching()
    {
        var client = Client();

        var result = await Plugin(client)
            .GetArticleSummaryAsync(" ", "en", CancellationToken.None);

        result.ShouldContain("Provide an article title");
        await client.DidNotReceive().GetSummaryAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSummary_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetSummaryAsync("Alan Turing", "en", Arg.Any<CancellationToken>())
            .Returns<WikipediaArticleSummary?>(
                _ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetArticleSummaryAsync("Alan Turing", "en", CancellationToken.None));

        result.ShouldBe("Wikipedia request timed out. Try again later.");
    }

    [Fact]
    public async Task GetSummary_ApiException_Returns_Its_UserSafe_Message()
    {
        var client = Client();
        client.GetSummaryAsync("Alan Turing", "en", Arg.Any<CancellationToken>())
            .Returns<WikipediaArticleSummary?>(
                _ => throw new WikipediaApiException("Wikipedia is currently unavailable. Try again later."));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetArticleSummaryAsync("Alan Turing", "en", CancellationToken.None));

        result.ShouldBe("Wikipedia is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task OnThisDay_Formats_Events()
    {
        var client = Client();
        client.GetOnThisDayAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WikipediaOnThisDayEvent>
            {
                new("Apollo 11 launches.", 1969),
                new("An undated event.", null),
            });

        var result = await Plugin(client).GetOnThisDayAsync(CancellationToken.None);

        result.ShouldContain("1969: Apollo 11 launches.");
        result.ShouldContain("An undated event.");
    }

    [Fact]
    public async Task OnThisDay_Empty_Returns_Friendly_Line()
    {
        var client = Client();
        client.GetOnThisDayAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WikipediaOnThisDayEvent>());

        var result = await Plugin(client).GetOnThisDayAsync(CancellationToken.None);

        result.ShouldBe("No notable historical events found for today. Try again later.");
    }

    [Fact]
    public async Task OnThisDay_Failure_Returns_Friendly_Line_Without_Throwing()
    {
        var client = Client();
        client.GetOnThisDayAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WikipediaOnThisDayEvent>>(
                _ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetOnThisDayAsync(CancellationToken.None));

        result.ShouldBe("Could not reach Wikipedia. Try again later.");
    }

    [Fact]
    public void Ctor_Throws_When_Client_Null()
    {
        Should.Throw<ArgumentNullException>(() => new WikipediaPlugin(null!));
    }
}
