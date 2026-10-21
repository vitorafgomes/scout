using Scout.Plugin.WorldClock;
using Scout.Plugin.WorldClock.Tests.Fakes;

namespace Scout.Plugin.WorldClock.Tests;

public sealed class WorldClockPluginTests
{
    private const string London = "Europe/London";

    // Friday 2026-07-03, midday UTC: London is on BST (+01:00), São Paulo on -03:00.
    private static readonly DateTimeOffset Now =
        new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    private static WorldClockPlugin Plugin(string defaultZone = London) =>
        new(new FixedTimeProvider(Now), new CityZoneResolver(), defaultZone);

    [Fact]
    public async Task CurrentTime_Reports_City_Time_Date_And_Offset()
    {
        var result = await Plugin().CurrentTimeInCityAsync("London", CancellationToken.None);

        result.ShouldBe(
            "It is 13:00 on Friday 2026-07-03 in London, United Kingdom (Europe/London, UTC+01:00).");
    }

    [Fact]
    public async Task CurrentTime_Accepts_Portuguese_Spelling_With_Diacritics()
    {
        var result = await Plugin().CurrentTimeInCityAsync("são paulo", CancellationToken.None);

        result.ShouldBe(
            "It is 09:00 on Friday 2026-07-03 in São Paulo, Brazil (America/Sao_Paulo, UTC-03:00).");
    }

    [Fact]
    public async Task CurrentTime_Accepts_Portuguese_City_Alias()
    {
        var result = await Plugin().CurrentTimeInCityAsync("nova york", CancellationToken.None);

        result.ShouldBe(
            "It is 08:00 on Friday 2026-07-03 in New York, United States (America/New_York, UTC-04:00).");
    }

    [Fact]
    public async Task CurrentTime_Empty_City_Uses_Configured_Home_Zone()
    {
        var result = await Plugin().CurrentTimeInCityAsync(string.Empty, CancellationToken.None);

        result.ShouldBe("It is 13:00 on Friday 2026-07-03 in Europe/London (UTC+01:00).");
    }

    [Fact]
    public async Task CurrentTime_Unknown_City_Returns_Corrective_Hint()
    {
        var result = await Plugin().CurrentTimeInCityAsync("Atlantis", CancellationToken.None);

        result.ShouldBe(
            "I could not find \"Atlantis\". Use a city like \"London\", or a city and country like \"Porto, Portugal\".");
    }

    [Fact]
    public async Task Convert_Same_Day_Reports_Target_Time()
    {
        var result = await Plugin()
            .ConvertTimeBetweenCitiesAsync("12:00", "São Paulo", "London", CancellationToken.None);

        result.ShouldBe(
            "12:00 in São Paulo, Brazil (America/Sao_Paulo) is 16:00 in London, United Kingdom (Europe/London).");
    }

    [Fact]
    public async Task Convert_Rolling_Back_A_Day_Is_Flagged()
    {
        var result = await Plugin()
            .ConvertTimeBetweenCitiesAsync("09:00", "Tokyo", "Los Angeles", CancellationToken.None);

        result.ShouldBe(
            "09:00 in Tokyo, Japan (Asia/Tokyo) is 17:00 in Los Angeles, United States (America/Los_Angeles) (previous day).");
    }

    [Fact]
    public async Task Convert_Accepts_Twelve_Hour_Time()
    {
        var result = await Plugin()
            .ConvertTimeBetweenCitiesAsync("3pm", "São Paulo", "London", CancellationToken.None);

        result.ShouldBe(
            "15:00 in São Paulo, Brazil (America/Sao_Paulo) is 19:00 in London, United Kingdom (Europe/London).");
    }

    [Fact]
    public async Task Convert_Invalid_Time_Returns_Time_Hint()
    {
        var result = await Plugin()
            .ConvertTimeBetweenCitiesAsync("half past", "São Paulo", "London", CancellationToken.None);

        result.ShouldBe("Give a time like 15:00, 9:30, or 3pm.");
    }

    [Fact]
    public async Task Convert_Unknown_Source_City_Returns_Corrective_Hint()
    {
        var result = await Plugin()
            .ConvertTimeBetweenCitiesAsync("12:00", "Atlantis", "London", CancellationToken.None);

        result.ShouldContain("I could not find \"Atlantis\"");
    }

    [Fact]
    public async Task TimeDifference_Reports_Hours_Ahead()
    {
        var result = await Plugin()
            .TimeDifferenceBetweenCitiesAsync("London", "São Paulo", CancellationToken.None);

        result.ShouldBe("London, United Kingdom is 4 hours ahead of São Paulo, Brazil.");
    }

    [Fact]
    public async Task TimeDifference_Reports_Hours_Behind()
    {
        var result = await Plugin()
            .TimeDifferenceBetweenCitiesAsync("São Paulo", "London", CancellationToken.None);

        result.ShouldBe("São Paulo, Brazil is 4 hours behind London, United Kingdom.");
    }

    [Fact]
    public async Task TimeDifference_Reports_Half_Hour_Zones()
    {
        var result = await Plugin()
            .TimeDifferenceBetweenCitiesAsync("London", "Mumbai", CancellationToken.None);

        result.ShouldBe("London, United Kingdom is 4 hours 30 minutes behind Mumbai, India.");
    }

    [Fact]
    public async Task TimeDifference_Same_Offset_Reports_Same_Time()
    {
        var result = await Plugin()
            .TimeDifferenceBetweenCitiesAsync("London", "Dublin", CancellationToken.None);

        result.ShouldBe("London, United Kingdom and Dublin, Ireland have the same time.");
    }

    [Fact]
    public async Task TimeDifference_Unknown_City_Returns_Corrective_Hint()
    {
        var result = await Plugin()
            .TimeDifferenceBetweenCitiesAsync("London", "Atlantis", CancellationToken.None);

        result.ShouldContain("I could not find \"Atlantis\"");
    }

    [Fact]
    public async Task Cancellation_Returns_Clean_Error_Without_Throwing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var plugin = Plugin();
        string result = string.Empty;

        await Should.NotThrowAsync(async () =>
            result = await plugin.CurrentTimeInCityAsync("London", cts.Token));

        result.ShouldBe("The time lookup was cancelled.");
    }

    [Fact]
    public void Ctor_Throws_When_Clock_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new WorldClockPlugin(null!, new CityZoneResolver(), London));
    }

    [Fact]
    public void Ctor_Throws_When_Resolver_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new WorldClockPlugin(new FixedTimeProvider(Now), null!, London));
    }

    [Fact]
    public void Ctor_Throws_When_DefaultTimeZone_Blank()
    {
        Should.Throw<ArgumentException>(
            () => new WorldClockPlugin(new FixedTimeProvider(Now), new CityZoneResolver(), "  "));
    }
}
