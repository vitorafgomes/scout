using System.Net;
using System.Text.Json;
using Scout.Plugin.Wikipedia;
using Scout.Plugin.Wikipedia.Tests.Fakes;

namespace Scout.Plugin.Wikipedia.Tests;

public sealed class WikipediaClientTests
{
    private static (WikipediaClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        return (new WikipediaClient(http), handler);
    }

    [Fact]
    public async Task Search_Targets_Language_Host_And_Sends_UserAgent()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"query":{"search":[]}}""");

        await client.SearchAsync("gato", "pt", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.RequestUri!.ToString().ShouldContain("pt.wikipedia.org");
        req.RequestUri!.ToString().ShouldContain("srsearch=gato");
        req.RequestUri!.ToString().ShouldContain("srlimit=5");
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
    }

    [Fact]
    public async Task Search_Parses_And_Strips_Markup_From_Snippet()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {"query":{"search":[
              {"title":"Alan Turing","snippet":"<span class=\"searchmatch\">Alan</span> Turing was a  mathematician &amp; codebreaker"}
            ]}}
            """);

        var results = await client.SearchAsync("Alan Turing", "en", CancellationToken.None);

        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("Alan Turing");
        // Markup stripped, entity decoded, whitespace collapsed to one line.
        results[0].Snippet.ShouldBe("Alan Turing was a mathematician & codebreaker");
        results[0].Snippet.ShouldNotContain("<span");
    }

    [Fact]
    public async Task Summary_Parses_Extract_And_Url()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {"title":"Alan Turing","extract":"Alan Turing was a mathematician.",
             "content_urls":{"desktop":{"page":"https://en.wikipedia.org/wiki/Alan_Turing"}}}
            """);

        var summary = await client.GetSummaryAsync("Alan Turing", "en", CancellationToken.None);

        summary.ShouldNotBeNull();
        summary!.Title.ShouldBe("Alan Turing");
        summary.Extract.ShouldBe("Alan Turing was a mathematician.");
        summary.Url.ShouldBe("https://en.wikipedia.org/wiki/Alan_Turing");
        handler.LastRequest!.RequestUri!.AbsoluteUri
            .ShouldContain("api/rest_v1/page/summary/Alan%20Turing");
    }

    [Fact]
    public async Task Summary_Caps_Extract_At_1500_Chars()
    {
        var (client, handler) = Build();
        var longExtract = new string('a', 3000);
        var json =
            "{\"title\":\"Long\",\"extract\":\"" + longExtract +
            "\",\"content_urls\":{\"desktop\":{\"page\":\"https://x\"}}}";
        handler.EnqueueJson(HttpStatusCode.OK, json);

        var summary = await client.GetSummaryAsync("Long", "en", CancellationToken.None);

        summary.ShouldNotBeNull();
        summary!.Extract.Length.ShouldBe(1500);
    }

    [Fact]
    public async Task Summary_NotFound_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, """{"type":"not_found"}""");

        var summary = await client.GetSummaryAsync("Nonexistent Article", "en", CancellationToken.None);

        summary.ShouldBeNull();
    }

    [Fact]
    public async Task OnThisDay_Targets_Feed_And_Parses_Events()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {"events":[
              {"year":1969,"text":"Apollo 11 launches."},
              {"year":1215,"text":"Magna Carta is sealed."},
              {"text":"An undated event."}
            ]}
            """);

        var events = await client.GetOnThisDayAsync(CancellationToken.None);

        events.Count.ShouldBe(3);
        events[0].Year.ShouldBe(1969);
        events[0].Text.ShouldBe("Apollo 11 launches.");
        events[2].Year.ShouldBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .ShouldContain("onthisday/events/");
    }

    [Fact]
    public async Task OnThisDay_Caps_At_Five_Events()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {"events":[
              {"year":1,"text":"e1"},{"year":2,"text":"e2"},{"year":3,"text":"e3"},
              {"year":4,"text":"e4"},{"year":5,"text":"e5"},{"year":6,"text":"e6"}
            ]}
            """);

        var events = await client.GetOnThisDayAsync(CancellationToken.None);

        events.Count.ShouldBe(5);
    }

    [Fact]
    public async Task RateLimited_429_Throws_RateLimit_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Should.ThrowAsync<WikipediaApiException>(
            () => client.SearchAsync("gato", "en", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.Message.ShouldContain("rate limit");
    }

    [Fact]
    public async Task ServerError_5xx_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<WikipediaApiException>(
            () => client.GetOnThisDayAsync(CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Timeout_Propagates_As_TaskCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.SearchAsync("gato", "en", CancellationToken.None));
    }

    [Fact]
    public async Task Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetSummaryAsync("Alan Turing", "en", CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_Body_Throws_JsonException()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{ this is not json ");

        await Should.ThrowAsync<JsonException>(
            () => client.SearchAsync("gato", "en", CancellationToken.None));
    }
}
