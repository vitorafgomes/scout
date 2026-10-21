using System.Net;
using Scout.Plugin.WhatToWatch;
using Scout.Plugin.WhatToWatch.Tests.Fakes;

namespace Scout.Plugin.WhatToWatch.Tests;

public sealed class WhatToWatchClientTests
{
    private static (TvMazeClient client, StubHttpMessageHandler handler) BuildTvMaze()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.tvmaze.com/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return (new TvMazeClient(http), handler);
    }

    private static (TmdbClient client, StubHttpMessageHandler handler) BuildTmdb()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.themoviedb.org/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return (new TmdbClient(http, "tmdb_key"), handler);
    }

    // --- TVMaze: search ---

    [Fact]
    public async Task SearchShows_Sends_Expected_Headers_And_Path()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueJson(HttpStatusCode.OK, "[]");

        await client.SearchShowsAsync("Breaking Bad", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.RequestUri!.AbsoluteUri.ShouldContain("search/shows?q=Breaking%20Bad");
    }

    [Fact]
    public async Task SearchShows_Parses_Items()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            [{"score":0.9,"show":{"name":"Breaking Bad","premiered":"2008-01-20","status":"Ended","genres":["Drama","Crime"],"rating":{"average":9.2},"network":{"name":"AMC"},"webChannel":null}}]
            """);

        var shows = await client.SearchShowsAsync("Breaking Bad", CancellationToken.None);

        shows.Count.ShouldBe(1);
        shows[0].Name.ShouldBe("Breaking Bad");
        shows[0].Year.ShouldBe(2008);
        shows[0].Status.ShouldBe("Ended");
        shows[0].Network.ShouldBe("AMC");
        shows[0].Genres.ShouldContain("Crime");
        shows[0].Rating.ShouldBe(9.2);
    }

    [Fact]
    public async Task SearchShows_Falls_Back_To_WebChannel_For_Network()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            [{"show":{"name":"Severance","premiered":"2022-02-18","status":"Running","genres":["Drama"],"rating":{"average":8.7},"network":null,"webChannel":{"name":"Apple TV+"}}}]
            """);

        var shows = await client.SearchShowsAsync("Severance", CancellationToken.None);

        shows[0].Network.ShouldBe("Apple TV+");
    }

    [Fact]
    public async Task SearchShows_Empty_Array_Returns_Empty_List()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueJson(HttpStatusCode.OK, "[]");

        var shows = await client.SearchShowsAsync("zzzz", CancellationToken.None);

        shows.ShouldBeEmpty();
    }

    // --- TVMaze: next episode ---

    [Fact]
    public async Task GetNextEpisode_Parses_Embedded_Episode()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {"name":"Severance","status":"Running","_embedded":{"nextepisode":{"season":2,"number":3,"name":"The Suitcase","airdate":"2026-08-01"}}}
            """);

        var result = await client.GetNextEpisodeAsync("Severance", CancellationToken.None);

        result.ShouldNotBeNull();
        result!.ShowName.ShouldBe("Severance");
        result.NextEpisode.ShouldNotBeNull();
        result.NextEpisode!.Season.ShouldBe(2);
        result.NextEpisode.Number.ShouldBe(3);
        result.NextEpisode.Name.ShouldBe("The Suitcase");
        result.NextEpisode.AirDate.ShouldBe(new DateOnly(2026, 8, 1));
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("singlesearch/shows?q=Severance&embed=nextepisode");
    }

    [Fact]
    public async Task GetNextEpisode_Found_Without_Next_Returns_Null_Episode()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"name":"Breaking Bad","status":"Ended","_embedded":{}}""");

        var result = await client.GetNextEpisodeAsync("Breaking Bad", CancellationToken.None);

        result.ShouldNotBeNull();
        result!.ShowName.ShouldBe("Breaking Bad");
        result.NextEpisode.ShouldBeNull();
    }

    [Fact]
    public async Task GetNextEpisode_NotFound_Returns_Null()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueJson(HttpStatusCode.NotFound, "");

        var result = await client.GetNextEpisodeAsync("no-such-show", CancellationToken.None);

        result.ShouldBeNull();
    }

    // --- TVMaze: recommend ---

    [Fact]
    public async Task RecommendShows_Filters_By_Genre_And_Sorts_By_Rating()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            [
              {"name":"Low Drama","premiered":"2010-01-01","status":"Ended","genres":["Drama"],"rating":{"average":6.0},"network":{"name":"A"}},
              {"name":"High Drama","premiered":"2012-01-01","status":"Running","genres":["Drama","Thriller"],"rating":{"average":9.1},"network":{"name":"B"}},
              {"name":"Pure Comedy","premiered":"2015-01-01","status":"Running","genres":["Comedy"],"rating":{"average":9.9},"network":{"name":"C"}}
            ]
            """);

        var shows = await client.RecommendShowsAsync("drama", CancellationToken.None);

        shows.Count.ShouldBe(2);
        shows[0].Name.ShouldBe("High Drama");
        shows[1].Name.ShouldBe("Low Drama");
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("shows?page=0");
    }

    // --- TVMaze: boundary failures ---

    [Fact]
    public async Task TvMaze_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<WhatToWatchApiException>(
            () => client.SearchShowsAsync("Breaking Bad", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task TvMaze_Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueJson(HttpStatusCode.OK, "{ this is not valid json");

        var ex = await Should.ThrowAsync<WhatToWatchApiException>(
            () => client.SearchShowsAsync("Breaking Bad", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task TvMaze_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.SearchShowsAsync("Breaking Bad", CancellationToken.None));
    }

    [Fact]
    public async Task TvMaze_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = BuildTvMaze();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.RecommendShowsAsync("Drama", CancellationToken.None));
    }

    // --- TMDB ---

    [Fact]
    public async Task Tmdb_Appends_ApiKey_And_Resolves_Providers()
    {
        var (client, handler) = BuildTmdb();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"results":[{"id":1396,"name":"The Bear"}]}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {"id":1396,"results":{"GB":{"flatrate":[{"provider_name":"Disney Plus"}],"rent":[{"provider_name":"Apple TV"}],"buy":[{"provider_name":"Amazon"}]}}}
            """);

        var availability = await client.GetWatchProvidersAsync("The Bear", "GB", CancellationToken.None);

        availability.ShouldNotBeNull();
        availability!.Title.ShouldBe("The Bear");
        availability.Country.ShouldBe("GB");
        availability.Streaming.ShouldContain("Disney Plus");
        availability.Rent.ShouldContain("Apple TV");
        availability.Buy.ShouldContain("Amazon");

        handler.RequestedUris[0].AbsoluteUri.ShouldContain("3/search/tv?query=The%20Bear");
        handler.RequestedUris[0].AbsoluteUri.ShouldContain("api_key=tmdb_key");
        handler.RequestedUris[1].AbsoluteUri.ShouldContain("3/tv/1396/watch/providers");
    }

    [Fact]
    public async Task Tmdb_No_Search_Match_Returns_Null()
    {
        var (client, handler) = BuildTmdb();
        handler.EnqueueJson(HttpStatusCode.OK, """{"results":[]}""");

        var availability = await client.GetWatchProvidersAsync("no-such-show", "GB", CancellationToken.None);

        availability.ShouldBeNull();
    }

    [Fact]
    public async Task Tmdb_Country_Absent_Returns_Empty_Lists()
    {
        var (client, handler) = BuildTmdb();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"results":[{"id":1396,"name":"The Bear"}]}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"id":1396,"results":{"US":{"flatrate":[{"provider_name":"Hulu"}]}}}""");

        var availability = await client.GetWatchProvidersAsync("The Bear", "GB", CancellationToken.None);

        availability.ShouldNotBeNull();
        availability!.Streaming.ShouldBeEmpty();
        availability.Rent.ShouldBeEmpty();
        availability.Buy.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tmdb_Unauthorized_Throws_Auth_Message()
    {
        var (client, handler) = BuildTmdb();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, """{"status_message":"invalid key"}""");

        var ex = await Should.ThrowAsync<WhatToWatchApiException>(
            () => client.GetWatchProvidersAsync("The Bear", "GB", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.Message.ShouldContain("authentication failed");
    }

    [Fact]
    public async Task Tmdb_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = BuildTmdb();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetWatchProvidersAsync("The Bear", "GB", CancellationToken.None));
    }

    [Fact]
    public async Task Tmdb_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = BuildTmdb();
        handler.EnqueueJson(HttpStatusCode.BadGateway, "{}");

        var ex = await Should.ThrowAsync<WhatToWatchApiException>(
            () => client.GetWatchProvidersAsync("The Bear", "GB", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public void Tmdb_Blank_Key_Throws_At_Construction()
    {
        var http = new HttpClient(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://api.themoviedb.org/"),
        };

        Should.Throw<ArgumentException>(() => new TmdbClient(http, ""));
    }
}
