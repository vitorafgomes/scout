using System.Net;
using Microsoft.Extensions.Options;
using Scout.Plugin.WebSearch;
using Scout.Plugin.WebSearch.Tests.Fakes;

namespace Scout.Plugin.WebSearch.Tests;

public sealed class WebSearchClientTests
{
    private static (WebSearchClient client, StubHttpMessageHandler handler) Build(string searxUrl = "https://searx.local")
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        var options = Options.Create(new WebSearchOptions { SearxUrl = searxUrl });
        return (new WebSearchClient(http, options), handler);
    }

    // ---- search (SearXNG) -------------------------------------------------

    [Fact]
    public async Task Search_Sends_UserAgent_And_Searx_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"results":[]}""");

        await client.SearchAsync("dotnet 10", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.RequestUri!.AbsoluteUri.ShouldContain("searx.local/search");
        req.RequestUri!.AbsoluteUri.ShouldContain("format=json");
        req.RequestUri!.AbsoluteUri.ShouldContain("q=dotnet%2010");
    }

    [Fact]
    public async Task Search_Parses_Results()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"results":[{"title":"T1","url":"https://a","content":"C1"},{"title":"T2","url":"https://b","content":"C2"}]}""");

        var results = await client.SearchAsync("q", CancellationToken.None);

        results.Count.ShouldBe(2);
        results[0].Title.ShouldBe("T1");
        results[0].Url.ShouldBe("https://a");
        results[0].Snippet.ShouldBe("C1");
    }

    [Fact]
    public async Task Search_Without_Searx_Throws_Not_Configured()
    {
        var (client, _) = Build(searxUrl: "");

        var ex = await Should.ThrowAsync<WebSearchApiException>(
            () => client.SearchAsync("q", CancellationToken.None));

        ex.Message.ShouldContain("not configured");
    }

    [Fact]
    public async Task Search_ServerError_Throws_Unavailable()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<WebSearchApiException>(
            () => client.SearchAsync("q", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Search_RateLimited_Throws_RateLimit()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Should.ThrowAsync<WebSearchApiException>(
            () => client.SearchAsync("q", CancellationToken.None));

        ex.Message.ShouldContain("rate limiting");
    }

    [Fact]
    public async Task Search_Malformed_Json_Throws_Unreadable()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{ this is not valid json");

        var ex = await Should.ThrowAsync<WebSearchApiException>(
            () => client.SearchAsync("q", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Search_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.SearchAsync("q", CancellationToken.None));
    }

    // ---- instant answer (DuckDuckGo) --------------------------------------

    [Fact]
    public async Task InstantAnswer_Sends_DuckDuckGo_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{}");

        await client.GetInstantAnswerAsync("capital of portugal", CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().ShouldContain("api.duckduckgo.com");
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("no_html=1");
    }

    [Fact]
    public async Task InstantAnswer_Parses_Abstract()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"Answer":"","AbstractText":"Lisbon is the capital of Portugal.","AbstractURL":"https://en.wikipedia.org/wiki/Lisbon"}""");

        var answer = await client.GetInstantAnswerAsync("capital of portugal", CancellationToken.None);

        answer.Text.ShouldBe("Lisbon is the capital of Portugal.");
        answer.Source.ShouldBe("https://en.wikipedia.org/wiki/Lisbon");
    }

    [Fact]
    public async Task InstantAnswer_Falls_Back_To_RelatedTopic()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"Answer":"","AbstractText":"","RelatedTopics":[{"Text":"A related topic.","FirstURL":"https://x"}]}""");

        var answer = await client.GetInstantAnswerAsync("q", CancellationToken.None);

        answer.Text.ShouldBe("A related topic.");
        answer.Source.ShouldBe("https://x");
    }

    [Fact]
    public async Task InstantAnswer_Empty_Payload_Returns_Empty_Answer()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{}");

        var answer = await client.GetInstantAnswerAsync("nonsense", CancellationToken.None);

        answer.Text.ShouldBeEmpty();
        answer.Source.ShouldBeEmpty();
    }

    [Fact]
    public async Task InstantAnswer_ServerError_Throws_Unavailable()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.BadGateway, "{}");

        var ex = await Should.ThrowAsync<WebSearchApiException>(
            () => client.GetInstantAnswerAsync("q", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task InstantAnswer_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetInstantAnswerAsync("q", CancellationToken.None));
    }

    [Fact]
    public async Task InstantAnswer_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetInstantAnswerAsync("q", CancellationToken.None));
    }

    // ---- fetch page -------------------------------------------------------

    [Fact]
    public async Task FetchPage_Html_Is_Readable()
    {
        var (client, handler) = Build();
        handler.EnqueueContent(HttpStatusCode.OK, "<html><body><p>Hello</p></body></html>", "text/html");

        var page = await client.FetchPageAsync("https://example.com/", CancellationToken.None);

        page.IsReadable.ShouldBeTrue();
        page.IsHtml.ShouldBeTrue();
        page.MediaType.ShouldBe("text/html");
        page.Content.ShouldContain("Hello");
    }

    [Fact]
    public async Task FetchPage_PlainText_Is_Readable_But_Not_Html()
    {
        var (client, handler) = Build();
        handler.EnqueueContent(HttpStatusCode.OK, "just text", "text/plain");

        var page = await client.FetchPageAsync("https://example.com/robots.txt", CancellationToken.None);

        page.IsReadable.ShouldBeTrue();
        page.IsHtml.ShouldBeFalse();
    }

    [Fact]
    public async Task FetchPage_NonText_Is_Not_Readable_And_Body_Not_Downloaded()
    {
        var (client, handler) = Build();
        handler.EnqueueContent(HttpStatusCode.OK, "%PDF-1.7 binary", "application/pdf");

        var page = await client.FetchPageAsync("https://example.com/file.pdf", CancellationToken.None);

        page.IsReadable.ShouldBeFalse();
        page.MediaType.ShouldBe("application/pdf");
        page.Content.ShouldBeEmpty();
    }

    [Fact]
    public async Task FetchPage_Oversized_Body_Is_Bounded()
    {
        var (client, handler) = Build();
        var big = "<html><body>" + new string('a', 600_000) + "</body></html>";
        handler.EnqueueContent(HttpStatusCode.OK, big, "text/html");

        var page = await client.FetchPageAsync("https://example.com/", CancellationToken.None);

        page.Content.Length.ShouldBeLessThanOrEqualTo(500_000);
    }

    [Fact]
    public async Task FetchPage_NotFound_Throws()
    {
        var (client, handler) = Build();
        handler.EnqueueContent(HttpStatusCode.NotFound, "", "text/html");

        var ex = await Should.ThrowAsync<WebSearchApiException>(
            () => client.FetchPageAsync("https://example.com/missing", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ex.Message.ShouldContain("could not be found");
    }

    [Fact]
    public async Task FetchPage_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.FetchPageAsync("https://example.com/", CancellationToken.None));
    }
}
