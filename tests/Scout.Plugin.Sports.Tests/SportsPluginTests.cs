using Microsoft.Extensions.Time.Testing;
using Scout.Plugin.Sports;
using Scout.Plugin.Sports.Abstractions;

namespace Scout.Plugin.Sports.Tests;

public sealed class SportsPluginTests
{
    private static ISportsClient Client() => Substitute.For<ISportsClient>();

    private static FakeTimeProvider ClockAt(int year, int month, int day)
        => new(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

    // Fix the time zone to UTC so season resolution is deterministic in tests.
    private static SportsOptions Options() => new() { TimeZone = "UTC" };

    private static SportsPlugin Plugin(ISportsClient client, TimeProvider clock)
        => new(client, clock, Options());

    private static SportsTeam Team(string id, string name, string league = "English Premier League", string country = "England")
        => new(id, name, league, country);

    private static SportsEvent Result(string date, string home, string away, int homeScore, int awayScore)
        => new($"{home} vs {away}", DateOnly.ParseExact(date, "yyyy-MM-dd"), "15:00:00", home, away, homeScore, awayScore, "English Premier League");

    private static SportsEvent Fixture(string date, string time, string home, string away)
        => new($"{home} vs {away}", DateOnly.ParseExact(date, "yyyy-MM-dd"), time, home, away, null, null, "English Premier League");

    private static SportsLeague League(string id, string name, string sport = "Soccer")
        => new(id, name, sport);

    // --- get_last_result ---

    [Fact]
    public async Task GetLastResult_Formats_Most_Recent_Finished_Match()
    {
        var client = Client();
        client.SearchTeamsAsync("Arsenal", Arg.Any<CancellationToken>())
            .Returns(new List<SportsTeam> { Team("133604", "Arsenal") });
        client.GetLastEventsAsync("133604", Arg.Any<CancellationToken>())
            .Returns(new List<SportsEvent>
            {
                Result("2026-05-01", "Arsenal", "Spurs", 1, 0),
                Result("2026-05-10", "Arsenal", "Chelsea", 2, 1),
            });

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLastResultAsync("Arsenal", CancellationToken.None);

        result.ShouldBe("Arsenal 2-1 Chelsea (English Premier League, 2026-05-10)");
    }

    [Fact]
    public async Task GetLastResult_Ignores_Unplayed_Fixtures()
    {
        var client = Client();
        client.SearchTeamsAsync("Arsenal", Arg.Any<CancellationToken>())
            .Returns(new List<SportsTeam> { Team("133604", "Arsenal") });
        client.GetLastEventsAsync("133604", Arg.Any<CancellationToken>())
            .Returns(new List<SportsEvent> { Fixture("2026-05-17", "14:00:00", "Arsenal", "Liverpool") });

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLastResultAsync("Arsenal", CancellationToken.None);

        result.ShouldBe("No recent result found for Arsenal.");
    }

    [Fact]
    public async Task GetLastResult_No_Team_Match_Returns_Corrective_Hint()
    {
        var client = Client();
        client.SearchTeamsAsync("Nowhere", Arg.Any<CancellationToken>())
            .Returns(new List<SportsTeam>());

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLastResultAsync("Nowhere", CancellationToken.None);

        result.ShouldContain("No team found matching 'Nowhere'");
        result.ShouldContain("Flamengo");
    }

    [Fact]
    public async Task GetLastResult_Ambiguous_Team_Returns_Did_You_Mean()
    {
        var client = Client();
        client.SearchTeamsAsync("City", Arg.Any<CancellationToken>())
            .Returns(new List<SportsTeam>
            {
                Team("1", "Manchester City", country: "England"),
                Team("2", "Melbourne City", country: "Australia"),
            });

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLastResultAsync("City", CancellationToken.None);

        result.ShouldStartWith("Did you mean:");
        result.ShouldContain("Manchester City (England)");
        result.ShouldContain("Melbourne City (Australia)");
        await client.DidNotReceiveWithAnyArgs().GetLastEventsAsync(default!, default);
    }

    [Fact]
    public async Task GetLastResult_Exact_Match_Wins_Over_Ambiguity()
    {
        var client = Client();
        client.SearchTeamsAsync("Arsenal", Arg.Any<CancellationToken>())
            .Returns(new List<SportsTeam>
            {
                Team("1", "Arsenal de Sarandi", country: "Argentina"),
                Team("133604", "Arsenal", country: "England"),
            });
        client.GetLastEventsAsync("133604", Arg.Any<CancellationToken>())
            .Returns(new List<SportsEvent> { Result("2026-05-10", "Arsenal", "Chelsea", 2, 1) });

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLastResultAsync("Arsenal", CancellationToken.None);

        result.ShouldContain("Arsenal 2-1 Chelsea");
    }

    [Fact]
    public async Task GetLastResult_Blank_Team_Returns_Teaching()
    {
        var client = Client();

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLastResultAsync("   ", CancellationToken.None);

        result.ShouldBe("Provide a team name like Flamengo or Arsenal.");
        await client.DidNotReceiveWithAnyArgs().SearchTeamsAsync(default!, default);
    }

    // --- get_next_fixture ---

    [Fact]
    public async Task GetNextFixture_Formats_Soonest_Upcoming_Match()
    {
        var client = Client();
        client.SearchTeamsAsync("Arsenal", Arg.Any<CancellationToken>())
            .Returns(new List<SportsTeam> { Team("133604", "Arsenal") });
        client.GetNextEventsAsync("133604", Arg.Any<CancellationToken>())
            .Returns(new List<SportsEvent>
            {
                Fixture("2026-05-24", "16:30:00", "Arsenal", "Everton"),
                Fixture("2026-05-17", "14:00:00", "Arsenal", "Liverpool"),
            });

        var result = await Plugin(client, ClockAt(2026, 5, 12))
            .GetNextFixtureAsync("Arsenal", CancellationToken.None);

        result.ShouldBe("Arsenal vs Liverpool - English Premier League, 2026-05-17 14:00");
    }

    [Fact]
    public async Task GetNextFixture_No_Upcoming_Returns_Message()
    {
        var client = Client();
        client.SearchTeamsAsync("Arsenal", Arg.Any<CancellationToken>())
            .Returns(new List<SportsTeam> { Team("133604", "Arsenal") });
        client.GetNextEventsAsync("133604", Arg.Any<CancellationToken>())
            .Returns(new List<SportsEvent>());

        var result = await Plugin(client, ClockAt(2026, 5, 12))
            .GetNextFixtureAsync("Arsenal", CancellationToken.None);

        result.ShouldBe("No upcoming fixture found for Arsenal.");
    }

    // --- get_league_table ---

    [Fact]
    public async Task GetLeagueTable_Formats_Rows_One_Per_Line()
    {
        var client = Client();
        client.ListLeaguesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SportsLeague> { League("4328", "English Premier League") });
        client.GetLeagueTableAsync("4328", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<LeagueStanding>
            {
                new(2, "Chelsea", 5, 3, 1, 1, 10),
                new(1, "Arsenal", 5, 4, 1, 0, 13),
            });

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLeagueTableAsync("English Premier League", CancellationToken.None);

        var lines = result.Split('\n');
        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("1. Arsenal - 13 pts (5 GP, 4W 1D 0L)");
        lines[1].ShouldBe("2. Chelsea - 10 pts (5 GP, 3W 1D 1L)");
    }

    [Fact]
    public async Task GetLeagueTable_Uses_Season_From_Clock_August_Onwards()
    {
        var client = Client();
        client.ListLeaguesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SportsLeague> { League("4328", "English Premier League") });
        client.GetLeagueTableAsync("4328", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<LeagueStanding> { new(1, "Arsenal", 1, 1, 0, 0, 3) });

        await Plugin(client, ClockAt(2026, 9, 1))
            .GetLeagueTableAsync("English Premier League", CancellationToken.None);

        await client.Received(1).GetLeagueTableAsync("4328", "2026-2027", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLeagueTable_Uses_Season_From_Clock_Before_August()
    {
        var client = Client();
        client.ListLeaguesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SportsLeague> { League("4328", "English Premier League") });
        client.GetLeagueTableAsync("4328", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<LeagueStanding> { new(1, "Arsenal", 1, 1, 0, 0, 3) });

        await Plugin(client, ClockAt(2026, 5, 1))
            .GetLeagueTableAsync("English Premier League", CancellationToken.None);

        await client.Received(1).GetLeagueTableAsync("4328", "2025-2026", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLeagueTable_Partial_Name_Resolves_Single_Match()
    {
        var client = Client();
        client.ListLeaguesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SportsLeague> { League("4328", "English Premier League") });
        client.GetLeagueTableAsync("4328", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<LeagueStanding> { new(1, "Arsenal", 1, 1, 0, 0, 3) });

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLeagueTableAsync("Premier League", CancellationToken.None);

        result.ShouldContain("1. Arsenal");
    }

    [Fact]
    public async Task GetLeagueTable_Unknown_League_Returns_Did_You_Mean()
    {
        var client = Client();
        client.ListLeaguesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SportsLeague>
            {
                League("4328", "English Premier League"),
                League("4330", "Scottish Premier League"),
            });

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLeagueTableAsync("Premier", CancellationToken.None);

        result.ShouldStartWith("Did you mean:");
        result.ShouldContain("English Premier League (Soccer)");
        result.ShouldContain("Scottish Premier League (Soccer)");
        await client.DidNotReceiveWithAnyArgs().GetLeagueTableAsync(default!, default!, default);
    }

    [Fact]
    public async Task GetLeagueTable_No_Fuzzy_Candidates_Returns_Static_Hint()
    {
        var client = Client();
        client.ListLeaguesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SportsLeague> { League("4328", "English Premier League") });

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLeagueTableAsync("Handball Cup", CancellationToken.None);

        result.ShouldBe(
            "No matching league found. Try Premier League, La Liga, Serie A, Bundesliga, or Brasileirao.");
        await client.DidNotReceiveWithAnyArgs().GetLeagueTableAsync(default!, default!, default);
    }

    [Fact]
    public async Task GetLeagueTable_Empty_Table_Returns_Message()
    {
        var client = Client();
        client.ListLeaguesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SportsLeague> { League("4328", "English Premier League") });
        client.GetLeagueTableAsync("4328", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<LeagueStanding>());

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLeagueTableAsync("English Premier League", CancellationToken.None);

        result.ShouldBe("No current league table available for English Premier League.");
    }

    [Fact]
    public async Task GetLeagueTable_Blank_League_Returns_Teaching()
    {
        var client = Client();

        var result = await Plugin(client, ClockAt(2026, 5, 20))
            .GetLeagueTableAsync("", CancellationToken.None);

        result.ShouldBe("Provide a league name like Premier League or Brasileirao Serie A.");
        await client.DidNotReceiveWithAnyArgs().ListLeaguesAsync(default);
    }

    // --- Failure handling: never throws into the kernel ---

    [Fact]
    public async Task GetLastResult_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.SearchTeamsAsync("Arsenal", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SportsTeam>>(
                _ => throw new SportsApiException("The sports service is currently unavailable. Try again later."));

        var plugin = Plugin(client, ClockAt(2026, 5, 20));

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetLastResultAsync("Arsenal", CancellationToken.None));

        result.ShouldBe("The sports service is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task GetNextFixture_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.SearchTeamsAsync("Arsenal", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SportsTeam>>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(client, ClockAt(2026, 5, 20));

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetNextFixtureAsync("Arsenal", CancellationToken.None));

        result.ShouldBe("The sports request timed out. Try again later.");
    }

    [Fact]
    public async Task GetLeagueTable_HttpRequestException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.ListLeaguesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SportsLeague>>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client, ClockAt(2026, 5, 20));

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetLeagueTableAsync("English Premier League", CancellationToken.None));

        result.ShouldBe("Could not reach the sports service. Try again later.");
    }

    [Fact]
    public void Ctor_Throws_When_Client_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new SportsPlugin(null!, ClockAt(2026, 5, 20), Options()));
    }

    [Fact]
    public void Ctor_Throws_When_Clock_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new SportsPlugin(Client(), null!, Options()));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new SportsPlugin(Client(), ClockAt(2026, 5, 20), null!));
    }
}
