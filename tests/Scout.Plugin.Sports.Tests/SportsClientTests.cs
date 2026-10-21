using System.Net;
using Scout.Plugin.Sports;
using Scout.Plugin.Sports.Tests.Fakes;

namespace Scout.Plugin.Sports.Tests;

public sealed class SportsClientTests
{
    private static (SportsClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.thesportsdb.com/api/v1/json/3/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return (new SportsClient(http), handler);
    }

    [Fact]
    public async Task SearchTeams_Sends_Expected_Headers_And_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"teams":null}""");

        await client.SearchTeamsAsync("Arsenal", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.RequestUri!.ToString().ShouldContain("searchteams.php?t=Arsenal");
    }

    [Fact]
    public async Task SearchTeams_Parses_Items()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"teams":[{"idTeam":"133604","strTeam":"Arsenal","strLeague":"English Premier League","strCountry":"England"}]}""");

        var teams = await client.SearchTeamsAsync("Arsenal", CancellationToken.None);

        teams.Count.ShouldBe(1);
        teams[0].Id.ShouldBe("133604");
        teams[0].Name.ShouldBe("Arsenal");
        teams[0].League.ShouldBe("English Premier League");
        teams[0].Country.ShouldBe("England");
    }

    [Fact]
    public async Task SearchTeams_Null_Array_Returns_Empty_List()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"teams":null}""");

        var teams = await client.SearchTeamsAsync("Nowhere United", CancellationToken.None);

        teams.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetLastEvents_Parses_Scores_And_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"results":[{"strEvent":"Arsenal vs Chelsea","dateEvent":"2026-05-10","strTime":"15:00:00","strHomeTeam":"Arsenal","strAwayTeam":"Chelsea","intHomeScore":"2","intAwayScore":"1","strLeague":"English Premier League"}]}""");

        var events = await client.GetLastEventsAsync("133604", CancellationToken.None);

        events.Count.ShouldBe(1);
        events[0].Date.ShouldBe(new DateOnly(2026, 5, 10));
        events[0].HomeScore.ShouldBe(2);
        events[0].AwayScore.ShouldBe(1);
        events[0].League.ShouldBe("English Premier League");
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("eventslast.php?id=133604");
    }

    [Fact]
    public async Task GetNextEvents_Parses_Fixture_With_Null_Scores_And_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"events":[{"strEvent":"Arsenal vs Liverpool","dateEvent":"2026-05-17","strTime":"14:00:00","strHomeTeam":"Arsenal","strAwayTeam":"Liverpool","intHomeScore":null,"intAwayScore":null,"strLeague":"English Premier League"}]}""");

        var events = await client.GetNextEventsAsync("133604", CancellationToken.None);

        events.Count.ShouldBe(1);
        events[0].HomeScore.ShouldBeNull();
        events[0].AwayScore.ShouldBeNull();
        events[0].Date.ShouldBe(new DateOnly(2026, 5, 17));
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("eventsnext.php?id=133604");
    }

    [Fact]
    public async Task ListLeagues_Parses_Items_And_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"leagues":[{"idLeague":"4328","strLeague":"English Premier League","strSport":"Soccer"}]}""");

        var leagues = await client.ListLeaguesAsync(CancellationToken.None);

        leagues.Count.ShouldBe(1);
        leagues[0].Id.ShouldBe("4328");
        leagues[0].Name.ShouldBe("English Premier League");
        leagues[0].Sport.ShouldBe("Soccer");
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("all_leagues.php");
    }

    [Fact]
    public async Task GetLeagueTable_Parses_Rows_And_Season_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"table":[{"intRank":"1","strTeam":"Arsenal","intPlayed":"5","intWin":"4","intDraw":"1","intLoss":"0","intPoints":"13"}]}""");

        var table = await client.GetLeagueTableAsync("4328", "2025-2026", CancellationToken.None);

        table.Count.ShouldBe(1);
        table[0].Rank.ShouldBe(1);
        table[0].Team.ShouldBe("Arsenal");
        table[0].Played.ShouldBe(5);
        table[0].Points.ShouldBe(13);
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("lookuptable.php?l=4328&s=2025-2026");
    }

    [Fact]
    public async Task GetLeagueTable_Empty_Season_Omits_Season_Query()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"table":null}""");

        var table = await client.GetLeagueTableAsync("4328", "", CancellationToken.None);

        table.ShouldBeEmpty();
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("lookuptable.php?l=4328");
        handler.LastRequest!.RequestUri!.ToString().ShouldNotContain("&s=");
    }

    // --- Boundary failure tests (TheSportsDB) ---

    [Fact]
    public async Task Unknown_Team_Empty_Result_Set_Returns_Empty_List()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"teams":null}""");

        var teams = await client.SearchTeamsAsync("Definitely Not A Club", CancellationToken.None);

        teams.ShouldBeEmpty();
    }

    [Fact]
    public async Task NotFound_Throws_NoMatch_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        var ex = await Should.ThrowAsync<SportsApiException>(
            () => client.GetLastEventsAsync("133604", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ex.Message.ShouldContain("No matching sports data");
    }

    [Fact]
    public async Task RateLimited_Throws_RateLimit_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Should.ThrowAsync<SportsApiException>(
            () => client.SearchTeamsAsync("Arsenal", CancellationToken.None));

        ex.Message.ShouldContain("rate limiting");
    }

    [Fact]
    public async Task ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<SportsApiException>(
            () => client.ListLeaguesAsync(CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{ this is not valid json");

        var ex = await Should.ThrowAsync<SportsApiException>(
            () => client.SearchTeamsAsync("Arsenal", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.SearchTeamsAsync("Arsenal", CancellationToken.None));
    }

    [Fact]
    public async Task Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetNextEventsAsync("133604", CancellationToken.None));
    }
}
