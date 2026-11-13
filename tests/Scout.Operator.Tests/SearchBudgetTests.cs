using Microsoft.Extensions.Time.Testing;

namespace Scout.Operator.Tests;

public sealed class SearchBudgetTests
{
    [Fact]
    public void Forty_Searches_A_Day_Are_Allowed_And_The_Next_One_Is_Refused()
    {
        var budget = new SearchBudget(new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero)));

        var taken = Enumerable.Range(0, 41).Select(_ => budget.TryTake()).ToList();

        taken.Count(t => t).ShouldBe(40);
        taken[^1].ShouldBeFalse();
    }

    [Fact]
    public void The_Budget_Resets_At_Midnight_Pacific_Time_Not_At_Midnight_Utc()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        var budget = new SearchBudget(clock);
        for (var i = 0; i < SearchBudget.PerDay; i++)
        {
            budget.TryTake();
        }

        clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 7, 59, 0, TimeSpan.Zero));
        var justBeforePacificMidnight = budget.TryTake();
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero));
        var atPacificMidnight = budget.TryTake();

        justBeforePacificMidnight.ShouldBeFalse("UTC midnight passed, but it is still 23:59 on 3 November in California");
        atPacificMidnight.ShouldBeTrue();
    }

    [Fact]
    public void Pacific_Time_Is_Found_By_Its_Windows_Id_When_The_Iana_Id_Is_Missing()
    {
        var windows = TimeZoneInfo.CreateCustomTimeZone("Pacific Standard Time", TimeSpan.FromHours(-8), "Pacific", "Pacific");
        var asked = new List<string>();

        var zone = SearchBudget.Pacific(id =>
        {
            asked.Add(id);
            return id == "Pacific Standard Time" ? windows : throw new TimeZoneNotFoundException(id);
        });

        zone.ShouldBeSameAs(windows);
        asked.ShouldBe(["America/Los_Angeles", "Pacific Standard Time"]);
    }

    [Fact]
    public void Without_Any_Time_Zone_Data_The_Budget_Resets_At_08_00_Utc_A_Fixed_Utc_Minus_8()
    {
        var zone = SearchBudget.Pacific(id => throw new TimeZoneNotFoundException(id));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        var budget = new SearchBudget(clock, zone);
        for (var i = 0; i < SearchBudget.PerDay; i++)
        {
            budget.TryTake();
        }

        clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 7, 59, 0, TimeSpan.Zero));
        var before = budget.TryTake();
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero));
        var after = budget.TryTake();

        zone.BaseUtcOffset.ShouldBe(TimeSpan.FromHours(-8));
        (before, after).ShouldBe((false, true));
    }
}
