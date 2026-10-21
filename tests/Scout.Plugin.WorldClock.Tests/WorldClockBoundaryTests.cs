using Scout.Plugin.WorldClock;
using Scout.Plugin.WorldClock.Tests.Fakes;

namespace Scout.Plugin.WorldClock.Tests;

/// <summary>
/// Production-readiness boundary tests. There is no network boundary to fail, so
/// the failure modes covered are: (1) unknown and ambiguous city input, which must
/// return a corrective sentence rather than a wrong answer or a throw; and (2)
/// daylight-saving correctness, pinned by moving a fake clock to instants just
/// before and just after both a spring-forward and a fall-back transition of
/// Europe/London and asserting the reported offset and difference flip.
/// São Paulo (fixed UTC-03:00, Brazil has no DST) is the stable anchor.
/// </summary>
public sealed class WorldClockBoundaryTests
{
    private const string London = "Europe/London";

    private static WorldClockPlugin PluginAt(DateTimeOffset instant) =>
        new(new FixedTimeProvider(instant), new CityZoneResolver(), London);

    // Europe/London springs forward at 01:00 UTC on 2026-03-29 (GMT -> BST).
    private static readonly DateTimeOffset BeforeSpringForward =
        new(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset AfterSpringForward =
        new(2026, 3, 29, 1, 30, 0, TimeSpan.Zero);

    // Europe/London falls back at 01:00 UTC on 2026-10-25 (BST -> GMT).
    private static readonly DateTimeOffset BeforeFallBack =
        new(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset AfterFallBack =
        new(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task SpringForward_Before_Transition_Reports_Standard_Offset()
    {
        var result = await PluginAt(BeforeSpringForward)
            .CurrentTimeInCityAsync("London", CancellationToken.None);

        result.ShouldContain("UTC+00:00");
    }

    [Fact]
    public async Task SpringForward_After_Transition_Reports_Daylight_Offset()
    {
        var result = await PluginAt(AfterSpringForward)
            .CurrentTimeInCityAsync("London", CancellationToken.None);

        result.ShouldContain("UTC+01:00");
    }

    [Fact]
    public async Task SpringForward_Difference_Flips_By_One_Hour()
    {
        var before = await PluginAt(BeforeSpringForward)
            .TimeDifferenceBetweenCitiesAsync("London", "São Paulo", CancellationToken.None);
        var after = await PluginAt(AfterSpringForward)
            .TimeDifferenceBetweenCitiesAsync("London", "São Paulo", CancellationToken.None);

        before.ShouldBe("London, United Kingdom is 3 hours ahead of São Paulo, Brazil.");
        after.ShouldBe("London, United Kingdom is 4 hours ahead of São Paulo, Brazil.");
    }

    [Fact]
    public async Task FallBack_Before_Transition_Reports_Daylight_Offset()
    {
        var result = await PluginAt(BeforeFallBack)
            .CurrentTimeInCityAsync("London", CancellationToken.None);

        result.ShouldContain("UTC+01:00");
    }

    [Fact]
    public async Task FallBack_After_Transition_Reports_Standard_Offset()
    {
        var result = await PluginAt(AfterFallBack)
            .CurrentTimeInCityAsync("London", CancellationToken.None);

        result.ShouldContain("UTC+00:00");
    }

    [Fact]
    public async Task FallBack_Difference_Flips_By_One_Hour()
    {
        var before = await PluginAt(BeforeFallBack)
            .TimeDifferenceBetweenCitiesAsync("London", "São Paulo", CancellationToken.None);
        var after = await PluginAt(AfterFallBack)
            .TimeDifferenceBetweenCitiesAsync("London", "São Paulo", CancellationToken.None);

        before.ShouldBe("London, United Kingdom is 4 hours ahead of São Paulo, Brazil.");
        after.ShouldBe("London, United Kingdom is 3 hours ahead of São Paulo, Brazil.");
    }

    [Fact]
    public async Task Convert_Is_Daylight_Saving_Aware_Across_Seasons()
    {
        // Same wall-clock conversion, evaluated with a summer and a winter clock.
        // In July London is BST (+01:00); in January it is GMT (+00:00).
        var summer = await PluginAt(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero))
            .ConvertTimeBetweenCitiesAsync("12:00", "São Paulo", "London", CancellationToken.None);
        var winter = await PluginAt(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero))
            .ConvertTimeBetweenCitiesAsync("12:00", "São Paulo", "London", CancellationToken.None);

        summer.ShouldBe(
            "12:00 in São Paulo, Brazil (America/Sao_Paulo) is 16:00 in London, United Kingdom (Europe/London).");
        winter.ShouldBe(
            "12:00 in São Paulo, Brazil (America/Sao_Paulo) is 15:00 in London, United Kingdom (Europe/London).");
    }

    [Fact]
    public async Task Ambiguous_Bare_City_Returns_Corrective_Hint()
    {
        // "San Jose" exists in both Costa Rica and the United States; a bare name
        // must not silently pick one.
        var result = await PluginAt(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero))
            .CurrentTimeInCityAsync("San Jose", CancellationToken.None);

        result.ShouldContain("Use a city like \"London\", or a city and country like \"Porto, Portugal\".");
    }

    [Fact]
    public async Task Ambiguous_City_Resolves_When_Qualified_With_Country()
    {
        var result = await PluginAt(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero))
            .CurrentTimeInCityAsync("San Jose, Costa Rica", CancellationToken.None);

        result.ShouldBe(
            "It is 06:00 on Friday 2026-07-03 in San Jose, Costa Rica (America/Costa_Rica, UTC-06:00).");
    }
}
