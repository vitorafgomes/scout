using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.News;
using Scout.Plugin.News.Abstractions;

namespace Scout.Plugin.News.Tests;

public sealed class NewsPluginTests : IDisposable
{
    private static readonly TimeProvider Clock =
        new FakeTimeProvider(new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero));

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "scout-news-tests", Guid.NewGuid().ToString("N"));

    private IScoutLocalStore Store() =>
        new JsonFileScoutLocalStore(Options.Create(new ScoutLocalStoreOptions { RootDirectory = _dir }));

    private static IFeedClient FeedClient() => Substitute.For<IFeedClient>();

    private static IHackerNewsClient HnClient() => Substitute.For<IHackerNewsClient>();

    private static NewsOptions OptionsWith(params (string Alias, string Url)[] feeds)
    {
        var options = new NewsOptions();
        foreach (var (alias, url) in feeds)
        {
            options.Feeds[alias] = url;
        }

        return options;
    }

    private NewsPlugin Plugin(
        IFeedClient? feed = null,
        IHackerNewsClient? hn = null,
        IScoutLocalStore? store = null,
        NewsOptions? options = null)
        => new(
            feed ?? FeedClient(),
            hn ?? HnClient(),
            store ?? Store(),
            Clock,
            options ?? OptionsWith(("bbc", "https://feeds.example.com/rss.xml")));

    private static FeedItem Item(string title, string link)
        => new(title, link, null);

    private static HackerNewsStory Story(string title, string url, int points)
        => new(title, url, points);

    [Fact]
    public async Task GetTopHeadlines_Formats_Items_With_Title_And_Link()
    {
        var feed = FeedClient();
        feed.GetFeedAsync("https://feeds.example.com/rss.xml", Arg.Any<CancellationToken>())
            .Returns(new List<FeedItem>
            {
                Item("First headline", "https://example.com/1"),
                Item("Second headline", "https://example.com/2"),
            });

        var result = await Plugin(feed: feed).GetTopHeadlinesAsync("bbc", CancellationToken.None);

        var lines = result.Split('\n');
        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("First headline - https://example.com/1");
        lines[1].ShouldBe("Second headline - https://example.com/2");
    }

    [Fact]
    public async Task GetTopHeadlines_Caps_At_Ten_Lines()
    {
        var feed = FeedClient();
        feed.GetFeedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 15).Select(i => Item($"Headline {i}", $"https://example.com/{i}")).ToList());

        var result = await Plugin(feed: feed).GetTopHeadlinesAsync("bbc", CancellationToken.None);

        result.Split('\n').Length.ShouldBe(10);
    }

    [Fact]
    public async Task GetTopHeadlines_Blank_Feed_Uses_Default()
    {
        var feed = FeedClient();
        feed.GetFeedAsync("https://feeds.example.com/rss.xml", Arg.Any<CancellationToken>())
            .Returns(new List<FeedItem> { Item("Default feed headline", "https://example.com/1") });

        var result = await Plugin(feed: feed).GetTopHeadlinesAsync("", CancellationToken.None);

        result.ShouldContain("Default feed headline");
        await feed.Received(1).GetFeedAsync("https://feeds.example.com/rss.xml", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTopHeadlines_Unknown_Feed_Returns_Teaching_With_Available()
    {
        var options = OptionsWith(("bbc", "https://a"), ("hn", "https://b"));

        var result = await Plugin(options: options).GetTopHeadlinesAsync("cnn", CancellationToken.None);

        result.ShouldContain("Unknown feed \"cnn\"");
        result.ShouldContain("bbc");
        result.ShouldContain("hn");
    }

    [Fact]
    public async Task GetTopHeadlines_No_Feeds_Configured_Returns_Operator_Hint()
    {
        var result = await Plugin(options: new NewsOptions()).GetTopHeadlinesAsync("bbc", CancellationToken.None);

        result.ShouldContain("No news feeds are configured");
        result.ShouldContain("Scout:Plugins:News:Feeds");
    }

    [Fact]
    public async Task GetTopHeadlines_Empty_Feed_Returns_Message()
    {
        var feed = FeedClient();
        feed.GetFeedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<FeedItem>());

        var result = await Plugin(feed: feed).GetTopHeadlinesAsync("bbc", CancellationToken.None);

        result.ShouldBe("No headlines found in the bbc feed.");
    }

    [Fact]
    public async Task GetTopHeadlines_Sanitizes_Multiline_Title()
    {
        var feed = FeedClient();
        feed.GetFeedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<FeedItem> { Item("Line one\n\n  Line two", "https://example.com/1") });

        var result = await Plugin(feed: feed).GetTopHeadlinesAsync("bbc", CancellationToken.None);

        result.ShouldBe("Line one Line two - https://example.com/1");
    }

    [Fact]
    public async Task GetTopHeadlines_Feed_Failure_Returns_Clean_Error_Without_Throwing()
    {
        var feed = FeedClient();
        feed.GetFeedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<FeedItem>>(
                _ => throw new NewsApiException("The news feed is currently unavailable. Try again later."));

        var plugin = Plugin(feed: feed);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetTopHeadlinesAsync("bbc", CancellationToken.None));

        result.ShouldBe("The news feed is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task GetTopHeadlines_Sanitizes_Newline_In_Link()
    {
        var feed = FeedClient();
        feed.GetFeedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<FeedItem>
            {
                Item("Real headline", "https://example.com/1\nFAKE HEADLINE - https://evil.example"),
            });

        var result = await Plugin(feed: feed).GetTopHeadlinesAsync("bbc", CancellationToken.None);

        // A crafted feed link must not smuggle an extra line into the output.
        result.Split('\n').Length.ShouldBe(1);
        result.ShouldStartWith("Real headline - https://example.com/1");
    }

    [Fact]
    public async Task GetHackerNewsTop_Sanitizes_Newline_In_Url()
    {
        var hn = HnClient();
        hn.GetTopStoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<HackerNewsStory>
            {
                Story("Real story", "https://example.com/s\nFAKE (999 points) - https://evil.example", 10),
            });

        var result = await Plugin(hn: hn).GetHackerNewsTopAsync(CancellationToken.None);

        result.Split('\n').Length.ShouldBe(1);
    }

    [Fact]
    public async Task SearchNews_Formats_Stories_With_Points()
    {
        var hn = HnClient();
        hn.SearchAsync("rust", Arg.Any<CancellationToken>())
            .Returns(new List<HackerNewsStory> { Story("Rust 2.0 released", "https://example.com/r", 250) });

        var result = await Plugin(hn: hn).SearchNewsAsync("rust", CancellationToken.None);

        result.ShouldBe("Rust 2.0 released (250 points) - https://example.com/r");
    }

    [Fact]
    public async Task SearchNews_Empty_Query_Returns_Corrective_Hint()
    {
        var result = await Plugin().SearchNewsAsync("   ", CancellationToken.None);

        result.ShouldContain("search_news");
        result.ShouldContain("search term");
    }

    [Fact]
    public async Task SearchNews_No_Results_Returns_Message()
    {
        var hn = HnClient();
        hn.SearchAsync("zzz", Arg.Any<CancellationToken>())
            .Returns(new List<HackerNewsStory>());

        var result = await Plugin(hn: hn).SearchNewsAsync("zzz", CancellationToken.None);

        result.ShouldBe("No news stories found for \"zzz\".");
    }

    [Fact]
    public async Task SearchNews_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var hn = HnClient();
        hn.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<HackerNewsStory>>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(hn: hn);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.SearchNewsAsync("x", CancellationToken.None));

        result.ShouldBe("The news request timed out. Try again later.");
    }

    [Fact]
    public async Task GetHackerNewsTop_Formats_Stories()
    {
        var hn = HnClient();
        hn.GetTopStoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<HackerNewsStory>
            {
                Story("Top story", "https://example.com/t", 1),
                Story("Second story", "https://example.com/s", 42),
            });

        var result = await Plugin(hn: hn).GetHackerNewsTopAsync(CancellationToken.None);

        var lines = result.Split('\n');
        lines[0].ShouldBe("Top story (1 point) - https://example.com/t");
        lines[1].ShouldBe("Second story (42 points) - https://example.com/s");
    }

    [Fact]
    public async Task GetHackerNewsTop_Empty_Returns_Message()
    {
        var hn = HnClient();
        hn.GetTopStoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<HackerNewsStory>());

        var result = await Plugin(hn: hn).GetHackerNewsTopAsync(CancellationToken.None);

        result.ShouldBe("No Hacker News stories available right now. Try again later.");
    }

    [Fact]
    public async Task GetHackerNewsTop_Transport_Failure_Returns_Clean_Error_Without_Throwing()
    {
        var hn = HnClient();
        hn.GetTopStoriesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<HackerNewsStory>>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(hn: hn);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetHackerNewsTopAsync(CancellationToken.None));

        result.ShouldBe("Could not reach the news service. Try again later.");
    }

    [Fact]
    public async Task SaveToReadingList_Saves_And_Is_Retrievable()
    {
        var store = Store();
        var plugin = Plugin(store: store);

        var saved = await plugin.SaveToReadingListAsync("https://example.com/article", "Great read");
        saved.ShouldBe("Saved r1 to your reading list.");

        var list = await plugin.GetReadingListAsync(CancellationToken.None);
        list.ShouldContain("r1");
        list.ShouldContain("Great read");
        list.ShouldContain("https://example.com/article");
    }

    [Fact]
    public async Task SaveToReadingList_Empty_Url_Returns_Corrective_Hint()
    {
        var result = await Plugin().SaveToReadingListAsync("   ");

        result.ShouldContain("save_to_reading_list");
    }

    [Fact]
    public async Task SaveToReadingList_Invalid_Url_Returns_Teaching()
    {
        var result = await Plugin().SaveToReadingListAsync("not a url");

        result.ShouldBe("Provide a valid http(s) link, for example: https://example.com/article.");
    }

    [Fact]
    public async Task SaveToReadingList_Rejects_Non_Http_Scheme()
    {
        var result = await Plugin().SaveToReadingListAsync("ftp://example.com/file");

        result.ShouldContain("valid http(s) link");
    }

    [Fact]
    public async Task SaveToReadingList_Store_Failure_Returns_Error_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.MutateAsync(
                Arg.Any<string>(),
                Arg.Any<Action<ReadingListDocument>>(),
                Arg.Any<CancellationToken>())
            .Returns<ReadingListDocument>(_ => throw new IOException("disk gone"));

        var plugin = Plugin(store: store);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.SaveToReadingListAsync("https://example.com/a"));

        result.ShouldBe("Could not access the local reading list. Try again.");
    }

    [Fact]
    public async Task GetReadingList_Empty_Returns_Message()
    {
        var result = await Plugin().GetReadingListAsync(CancellationToken.None);

        result.ShouldBe("Your reading list is empty.");
    }

    [Fact]
    public async Task GetReadingList_Newest_First_And_Capped()
    {
        var store = Store();
        var plugin = Plugin(store: store);
        for (var i = 1; i <= 12; i++)
        {
            await plugin.SaveToReadingListAsync($"https://example.com/{i}", $"Title {i}");
        }

        var result = await plugin.GetReadingListAsync(CancellationToken.None);
        var lines = result.Split('\n');

        lines.Length.ShouldBe(10);
        lines[0].ShouldContain("Title 12");
        lines[9].ShouldContain("Title 3");
    }

    [Fact]
    public async Task GetReadingList_Store_Failure_Returns_Error_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.ReadOrCreateAsync<ReadingListDocument>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ReadingListDocument>(_ => throw new UnauthorizedAccessException("denied"));

        var plugin = Plugin(store: store);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetReadingListAsync(CancellationToken.None));

        result.ShouldBe("Could not access the local reading list. Try again.");
    }

    [Fact]
    public void Ctor_Throws_When_FeedClient_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new NewsPlugin(null!, HnClient(), Store(), Clock, new NewsOptions()));
    }

    [Fact]
    public void Ctor_Throws_When_HackerNews_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new NewsPlugin(FeedClient(), null!, Store(), Clock, new NewsOptions()));
    }

    [Fact]
    public void Ctor_Throws_When_Store_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new NewsPlugin(FeedClient(), HnClient(), null!, Clock, new NewsOptions()));
    }

    [Fact]
    public void Ctor_Throws_When_Clock_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new NewsPlugin(FeedClient(), HnClient(), Store(), null!, new NewsOptions()));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new NewsPlugin(FeedClient(), HnClient(), Store(), Clock, null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
