using Microsoft.Extensions.Time.Testing;
using Scout.Plugin.WhatToWatch;
using Scout.Plugin.WhatToWatch.Abstractions;

namespace Scout.Plugin.WhatToWatch.Tests;

public sealed class WhatToWatchPluginTests
{
    private static ITvMazeClient TvMaze() => Substitute.For<ITvMazeClient>();

    private static ITmdbClient Tmdb() => Substitute.For<ITmdbClient>();

    private static FakeTimeProvider ClockAt(int year, int month, int day)
        => new(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

    // Fix the time zone to UTC so relative-date resolution is deterministic.
    private static WhatToWatchOptions Options() => new()
    {
        DefaultCountry = "GB",
        TimeZone = "UTC",
        MaxResults = 3,
    };

    private static WhatToWatchPlugin Plugin(
        ITvMazeClient tvMaze,
        ITmdbClient? tmdb = null,
        TimeProvider? clock = null)
        => new(tvMaze, tmdb, clock ?? ClockAt(2026, 6, 1), Options());

    private static Show ShowOf(string name, int? year, string status, string? network, double? rating, params string[] genres)
        => new(name, year, network, status, genres, rating);

    // --- search_show ---

    [Fact]
    public async Task SearchShow_Formats_Matches_And_Caps_Results()
    {
        var client = TvMaze();
        client.SearchShowsAsync("drama", Arg.Any<CancellationToken>())
            .Returns(new List<Show>
            {
                ShowOf("First", 2008, "Ended", "AMC", 9.2, "Drama", "Crime"),
                ShowOf("Second", 2012, "Running", "HBO", 8.5, "Drama"),
                ShowOf("Third", 2015, "Running", "Netflix", 8.0, "Drama"),
                ShowOf("Fourth", 2020, "Running", "Apple TV+", 7.5, "Drama"),
            });

        var result = await Plugin(client).SearchShowAsync("drama", CancellationToken.None);

        result.ShouldContain("First (2008) - Ended - AMC [Drama, Crime]");
        result.ShouldContain("Second");
        result.ShouldContain("Third");
        result.ShouldNotContain("Fourth");
    }

    [Fact]
    public async Task SearchShow_Empty_Returns_Message()
    {
        var client = TvMaze();
        client.SearchShowsAsync("zzzz", Arg.Any<CancellationToken>())
            .Returns(new List<Show>());

        var result = await Plugin(client).SearchShowAsync("zzzz", CancellationToken.None);

        result.ShouldBe("No shows found matching 'zzzz'.");
    }

    [Fact]
    public async Task SearchShow_Blank_Title_Returns_Teaching()
    {
        var client = TvMaze();

        var result = await Plugin(client).SearchShowAsync("   ", CancellationToken.None);

        result.ShouldBe("Provide a show title, e.g. Breaking Bad.");
        await client.DidNotReceiveWithAnyArgs().SearchShowsAsync(default!, default);
    }

    // --- get_next_episode ---

    [Fact]
    public async Task GetNextEpisode_Formats_With_Days_Away()
    {
        var client = TvMaze();
        client.GetNextEpisodeAsync("Severance", Arg.Any<CancellationToken>())
            .Returns(new ShowEpisode("Severance", new Episode(2, 3, "The Suitcase", new DateOnly(2026, 6, 2))));

        var result = await Plugin(client, clock: ClockAt(2026, 6, 1))
            .GetNextEpisodeAsync("Severance", CancellationToken.None);

        result.ShouldContain("Severance S02E03 - The Suitcase airs 2026-06-02");
        result.ShouldContain("in 1 day");
    }

    [Fact]
    public async Task GetNextEpisode_No_AirDate_Reports_TBA()
    {
        var client = TvMaze();
        client.GetNextEpisodeAsync("Severance", Arg.Any<CancellationToken>())
            .Returns(new ShowEpisode("Severance", new Episode(3, 1, "Unknown", null)));

        var result = await Plugin(client).GetNextEpisodeAsync("Severance", CancellationToken.None);

        result.ShouldContain("air date TBA");
    }

    [Fact]
    public async Task GetNextEpisode_Found_Without_Next_Returns_Message()
    {
        var client = TvMaze();
        client.GetNextEpisodeAsync("Breaking Bad", Arg.Any<CancellationToken>())
            .Returns(new ShowEpisode("Breaking Bad", null));

        var result = await Plugin(client).GetNextEpisodeAsync("Breaking Bad", CancellationToken.None);

        result.ShouldBe("Breaking Bad has no upcoming episode scheduled (it may be between seasons or ended).");
    }

    [Fact]
    public async Task GetNextEpisode_Unknown_Show_Returns_Message()
    {
        var client = TvMaze();
        client.GetNextEpisodeAsync("no-such", Arg.Any<CancellationToken>())
            .Returns((ShowEpisode?)null);

        var result = await Plugin(client).GetNextEpisodeAsync("no-such", CancellationToken.None);

        result.ShouldBe("No show found matching 'no-such' on TVMaze.");
    }

    // --- recommend_shows ---

    [Fact]
    public async Task RecommendShows_Formats_With_Rating()
    {
        var client = TvMaze();
        client.RecommendShowsAsync("Drama", Arg.Any<CancellationToken>())
            .Returns(new List<Show>
            {
                ShowOf("High Drama", 2012, "Running", "HBO", 9.1, "Drama", "Thriller"),
            });

        var result = await Plugin(client).RecommendShowsAsync("Drama", CancellationToken.None);

        result.ShouldBe("High Drama (2012) - rating 9.1 [Drama, Thriller]");
    }

    [Fact]
    public async Task RecommendShows_Missing_Rating_Reports_Na()
    {
        var client = TvMaze();
        client.RecommendShowsAsync("Drama", Arg.Any<CancellationToken>())
            .Returns(new List<Show> { ShowOf("Untitled", null, "Running", null, null, "Drama") });

        var result = await Plugin(client).RecommendShowsAsync("Drama", CancellationToken.None);

        result.ShouldContain("rating n/a");
        result.ShouldNotContain("(");
    }

    [Fact]
    public async Task RecommendShows_Empty_Returns_Message()
    {
        var client = TvMaze();
        client.RecommendShowsAsync("Polka", Arg.Any<CancellationToken>())
            .Returns(new List<Show>());

        var result = await Plugin(client).RecommendShowsAsync("Polka", CancellationToken.None);

        result.ShouldBe("No Polka shows found to recommend.");
    }

    [Fact]
    public async Task RecommendShows_Blank_Genre_Returns_Teaching()
    {
        var client = TvMaze();

        var result = await Plugin(client).RecommendShowsAsync("", CancellationToken.None);

        result.ShouldBe("Provide a genre, e.g. Drama.");
        await client.DidNotReceiveWithAnyArgs().RecommendShowsAsync(default!, default);
    }

    // --- where_to_watch ---

    [Fact]
    public async Task WhereToWatch_No_Tmdb_Client_Degrades_Gracefully()
    {
        var client = TvMaze();

        var result = await Plugin(client, tmdb: null)
            .WhereToWatchAsync("The Bear", "GB", CancellationToken.None);

        result.ShouldContain("needs a TMDB key");
        result.ShouldContain("Scout:Plugins:WhatToWatch:TmdbApiKey");
    }

    [Fact]
    public async Task WhereToWatch_Formats_Providers_By_Type()
    {
        var client = TvMaze();
        var tmdb = Tmdb();
        tmdb.GetWatchProvidersAsync("The Bear", "GB", Arg.Any<CancellationToken>())
            .Returns(new WatchAvailability(
                "The Bear", "GB",
                new[] { "Disney Plus" },
                new[] { "Apple TV" },
                new[] { "Amazon" }));

        var result = await Plugin(client, tmdb).WhereToWatchAsync("The Bear", "GB", CancellationToken.None);

        result.ShouldContain("The Bear in GB:");
        result.ShouldContain("Stream: Disney Plus");
        result.ShouldContain("Rent: Apple TV");
        result.ShouldContain("Buy: Amazon");
    }

    [Fact]
    public async Task WhereToWatch_Uppercases_Country()
    {
        var client = TvMaze();
        var tmdb = Tmdb();
        tmdb.GetWatchProvidersAsync("The Bear", "BR", Arg.Any<CancellationToken>())
            .Returns(new WatchAvailability("The Bear", "BR", new[] { "Star Plus" }, [], []));

        var result = await Plugin(client, tmdb).WhereToWatchAsync("The Bear", "br", CancellationToken.None);

        result.ShouldContain("Star Plus");
        await tmdb.Received(1).GetWatchProvidersAsync("The Bear", "BR", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhereToWatch_No_Providers_Returns_Message()
    {
        var client = TvMaze();
        var tmdb = Tmdb();
        tmdb.GetWatchProvidersAsync("The Bear", "GB", Arg.Any<CancellationToken>())
            .Returns(new WatchAvailability("The Bear", "GB", [], [], []));

        var result = await Plugin(client, tmdb).WhereToWatchAsync("The Bear", "GB", CancellationToken.None);

        result.ShouldBe("No streaming providers are listed for 'The Bear' in GB.");
    }

    [Fact]
    public async Task WhereToWatch_Unknown_Title_Returns_Message()
    {
        var client = TvMaze();
        var tmdb = Tmdb();
        tmdb.GetWatchProvidersAsync("no-such", "GB", Arg.Any<CancellationToken>())
            .Returns((WatchAvailability?)null);

        var result = await Plugin(client, tmdb).WhereToWatchAsync("no-such", "GB", CancellationToken.None);

        result.ShouldBe("No title matching 'no-such' was found on TMDB.");
    }

    [Fact]
    public async Task WhereToWatch_Invalid_Country_Returns_Teaching()
    {
        var client = TvMaze();
        var tmdb = Tmdb();

        var result = await Plugin(client, tmdb).WhereToWatchAsync("The Bear", "britain", CancellationToken.None);

        result.ShouldBe("Use ISO country codes like GB, BR, US.");
        await tmdb.DidNotReceiveWithAnyArgs().GetWatchProvidersAsync(default!, default!, default);
    }

    [Fact]
    public async Task WhereToWatch_Blank_Title_Returns_Teaching()
    {
        var client = TvMaze();
        var tmdb = Tmdb();

        var result = await Plugin(client, tmdb).WhereToWatchAsync("  ", "GB", CancellationToken.None);

        result.ShouldBe("Provide a show title, e.g. The Bear.");
    }

    // --- failure handling: functions never throw into the kernel ---

    [Fact]
    public async Task SearchShow_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = TvMaze();
        client.SearchShowsAsync("x", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Show>>(
                _ => throw new WhatToWatchApiException("The show database is currently unavailable. Try again later."));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.SearchShowAsync("x", CancellationToken.None));

        result.ShouldBe("The show database is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task GetNextEpisode_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var client = TvMaze();
        client.GetNextEpisodeAsync("x", Arg.Any<CancellationToken>())
            .Returns<ShowEpisode?>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetNextEpisodeAsync("x", CancellationToken.None));

        result.ShouldBe("The request to the show services timed out. Try again later.");
    }

    [Fact]
    public async Task RecommendShows_HttpRequestException_Returns_Clean_Error_Without_Throwing()
    {
        var client = TvMaze();
        client.RecommendShowsAsync("Drama", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Show>>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.RecommendShowsAsync("Drama", CancellationToken.None));

        result.ShouldBe("Could not reach the show services. Try again later.");
    }

    [Fact]
    public async Task WhereToWatch_TmdbException_Returns_Clean_Error_Without_Throwing()
    {
        var client = TvMaze();
        var tmdb = Tmdb();
        tmdb.GetWatchProvidersAsync("The Bear", "GB", Arg.Any<CancellationToken>())
            .Returns<WatchAvailability?>(
                _ => throw new WhatToWatchApiException("TMDB authentication failed. Check the configured TMDB key."));

        var plugin = Plugin(client, tmdb);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.WhereToWatchAsync("The Bear", "GB", CancellationToken.None));

        result.ShouldBe("TMDB authentication failed. Check the configured TMDB key.");
    }

    // --- constructor guards ---

    [Fact]
    public void Ctor_Throws_When_TvMaze_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new WhatToWatchPlugin(null!, ClockAt(2026, 6, 1), Options()));
    }

    [Fact]
    public void Ctor_Throws_When_Clock_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new WhatToWatchPlugin(TvMaze(), null!, Options()));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new WhatToWatchPlugin(TvMaze(), ClockAt(2026, 6, 1), null!));
    }
}
