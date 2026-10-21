using System.Net;
using Scout.Plugin.News;
using Scout.Plugin.News.Tests.Fakes;

namespace Scout.Plugin.News.Tests;

/// <summary>
/// Exercises both external boundaries over a stub handler: the RSS/Atom feed
/// boundary (<see cref="FeedClient"/>) and the Hacker News Algolia boundary
/// (<see cref="HackerNewsClient"/>). Covers happy-path parsing plus the failure
/// modes required for production readiness (timeout, 5xx, 404, malformed payload,
/// transport reset).
/// </summary>
public sealed class NewsClientTests
{
    private const string RssFeed =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0">
          <channel>
            <title>Example News</title>
            <item>
              <title>First headline</title>
              <link>https://example.com/1</link>
              <pubDate>Wed, 02 Jul 2026 08:00:00 GMT</pubDate>
            </item>
            <item>
              <title>Second headline</title>
              <link>https://example.com/2</link>
            </item>
          </channel>
        </rss>
        """;

    private const string AtomFeed =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Example Atom</title>
          <entry>
            <title>Atom headline</title>
            <link href="https://example.com/a" rel="alternate"/>
            <updated>2026-07-02T18:30:02Z</updated>
          </entry>
        </feed>
        """;

    private const string HnPayload =
        """
        {"hits":[
          {"title":"Show HN: A tiny database","url":"https://example.com/x","points":123,"objectID":"111"},
          {"title":"Ask HN: Why?","url":null,"points":5,"objectID":"222"}
        ]}
        """;

    private static (FeedClient client, StubHttpMessageHandler handler) BuildFeed()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        return (new FeedClient(http), handler);
    }

    private static (HackerNewsClient client, StubHttpMessageHandler handler) BuildHn()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://hn.algolia.com/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return (new HackerNewsClient(http), handler);
    }

    [Fact]
    public async Task Feed_Sends_UserAgent_And_Requested_Url()
    {
        var (client, handler) = BuildFeed();
        handler.EnqueueBody(HttpStatusCode.OK, RssFeed);

        await client.GetFeedAsync("https://feeds.example.com/rss.xml", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.RequestUri!.ToString().ShouldBe("https://feeds.example.com/rss.xml");
    }

    [Fact]
    public async Task Feed_Parses_Rss_Items()
    {
        var (client, handler) = BuildFeed();
        handler.EnqueueBody(HttpStatusCode.OK, RssFeed);

        var items = await client.GetFeedAsync("https://feeds.example.com/rss.xml", CancellationToken.None);

        items.Count.ShouldBe(2);
        items[0].Title.ShouldBe("First headline");
        items[0].Link.ShouldBe("https://example.com/1");
        items[0].Published.ShouldNotBeNull();
        items[1].Title.ShouldBe("Second headline");
        items[1].Published.ShouldBeNull();
    }

    [Fact]
    public async Task Feed_Parses_Atom_Entries()
    {
        var (client, handler) = BuildFeed();
        handler.EnqueueBody(HttpStatusCode.OK, AtomFeed);

        var items = await client.GetFeedAsync("https://feeds.example.com/atom.xml", CancellationToken.None);

        items.Count.ShouldBe(1);
        items[0].Title.ShouldBe("Atom headline");
        items[0].Link.ShouldBe("https://example.com/a");
        items[0].Published.ShouldNotBeNull();
    }

    [Fact]
    public async Task Feed_Empty_Channel_Returns_Empty_List()
    {
        var (client, handler) = BuildFeed();
        handler.EnqueueBody(HttpStatusCode.OK, "<rss version=\"2.0\"><channel><title>x</title></channel></rss>");

        var items = await client.GetFeedAsync("https://feeds.example.com/rss.xml", CancellationToken.None);

        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Feed_NotFound_Throws_NotFound_Message()
    {
        var (client, handler) = BuildFeed();
        handler.EnqueueBody(HttpStatusCode.NotFound, "");

        var ex = await Should.ThrowAsync<NewsApiException>(
            () => client.GetFeedAsync("https://feeds.example.com/missing.xml", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ex.Message.ShouldContain("could not be found");
    }

    [Fact]
    public async Task Feed_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = BuildFeed();
        handler.EnqueueBody(HttpStatusCode.InternalServerError, "");

        var ex = await Should.ThrowAsync<NewsApiException>(
            () => client.GetFeedAsync("https://feeds.example.com/rss.xml", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Feed_Malformed_Xml_Throws_Unreadable_Message()
    {
        var (client, handler) = BuildFeed();
        handler.EnqueueBody(HttpStatusCode.OK, "<rss><channel><item><title>oops");

        var ex = await Should.ThrowAsync<NewsApiException>(
            () => client.GetFeedAsync("https://feeds.example.com/rss.xml", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Feed_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = BuildFeed();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetFeedAsync("https://feeds.example.com/rss.xml", CancellationToken.None));
    }

    [Fact]
    public async Task Feed_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = BuildFeed();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetFeedAsync("https://feeds.example.com/rss.xml", CancellationToken.None));
    }

    [Fact]
    public async Task Hn_Top_Sends_FrontPage_Path()
    {
        var (client, handler) = BuildHn();
        handler.EnqueueBody(HttpStatusCode.OK, HnPayload);

        await client.GetTopStoriesAsync(CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.RequestUri!.ToString().ShouldContain("api/v1/search?tags=front_page");
    }

    [Fact]
    public async Task Hn_Top_Parses_Stories_And_Falls_Back_To_Item_Url()
    {
        var (client, handler) = BuildHn();
        handler.EnqueueBody(HttpStatusCode.OK, HnPayload);

        var stories = await client.GetTopStoriesAsync(CancellationToken.None);

        stories.Count.ShouldBe(2);
        stories[0].Title.ShouldBe("Show HN: A tiny database");
        stories[0].Url.ShouldBe("https://example.com/x");
        stories[0].Points.ShouldBe(123);
        // Null url falls back to the HN item page.
        stories[1].Url.ShouldBe("https://news.ycombinator.com/item?id=222");
    }

    [Fact]
    public async Task Hn_Search_Escapes_Query_And_Uses_Story_Tag()
    {
        var (client, handler) = BuildHn();
        handler.EnqueueBody(HttpStatusCode.OK, HnPayload);

        await client.SearchAsync("rust lang", CancellationToken.None);

        // AbsoluteUri preserves percent-encoding; ToString() would decode %20 for display.
        var url = handler.LastRequest!.RequestUri!.AbsoluteUri;
        url.ShouldContain("query=rust%20lang");
        url.ShouldContain("tags=story");
    }

    [Fact]
    public async Task Hn_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = BuildHn();
        handler.EnqueueBody(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<NewsApiException>(
            () => client.GetTopStoriesAsync(CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Hn_Malformed_Json_Throws_Unreadable_Message()
    {
        var (client, handler) = BuildHn();
        handler.EnqueueBody(HttpStatusCode.OK, "{ not valid json");

        var ex = await Should.ThrowAsync<NewsApiException>(
            () => client.SearchAsync("x", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Hn_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = BuildHn();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetTopStoriesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Hn_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = BuildHn();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.SearchAsync("x", CancellationToken.None));
    }
}
