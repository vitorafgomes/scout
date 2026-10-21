using Scout.Plugin.Dates;
using Scout.Plugin.Dates.Tests.Fakes;

namespace Scout.Plugin.Dates.Tests;

public sealed class DatesPluginTests
{
    private const string London = "Europe/London";

    // Friday 2026-07-03, midday UTC so the London date is unambiguous under BST.
    private static readonly DateTimeOffset Now =
        new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    private static DatesPlugin Plugin(string timeZone = London) =>
        new(new FixedTimeProvider(Now), timeZone);

    [Fact]
    public async Task Today_Reports_Weekday_And_Iso_Week()
    {
        var result = await Plugin().TodayAsync(string.Empty, CancellationToken.None);

        result.ShouldBe("Today is Friday 2026-07-03, week 27.");
    }

    [Fact]
    public async Task Today_Honours_Explicit_Timezone()
    {
        // A far-eastern zone is already on the next calendar day at 12:00 UTC.
        var result = await Plugin().TodayAsync("Pacific/Kiritimati", CancellationToken.None);

        result.ShouldBe("Today is Saturday 2026-07-04, week 27.");
    }

    [Fact]
    public async Task CurrentTime_Reports_Local_Time_Date_And_Offset()
    {
        // 12:00 UTC is 13:00 in London under BST (+01:00).
        var result = await Plugin().CurrentTimeAsync(string.Empty, CancellationToken.None);

        result.ShouldBe("It is 13:00 on Friday 2026-07-03 in Europe/London (UTC+01:00).");
    }

    [Fact]
    public async Task CurrentTime_Honours_Explicit_Timezone()
    {
        var result = await Plugin().CurrentTimeAsync("America/Sao_Paulo", CancellationToken.None);

        // 12:00 UTC is 09:00 in Sao Paulo (-03:00).
        result.ShouldBe("It is 09:00 on Friday 2026-07-03 in America/Sao_Paulo (UTC-03:00).");
    }

    [Fact]
    public async Task DaysUntil_Future_Counts_Forward()
    {
        var result = await Plugin().DaysUntilAsync("2026-12-25", CancellationToken.None);

        result.ShouldBe("175 days until 2026-12-25 (Friday).");
    }

    [Fact]
    public async Task DaysUntil_Past_Reports_Ago()
    {
        var result = await Plugin().DaysUntilAsync("2026-07-01", CancellationToken.None);

        result.ShouldBe("2 days ago (2026-07-01, Wednesday).");
    }

    [Fact]
    public async Task DaysUntil_Today_Reports_Today()
    {
        var result = await Plugin().DaysUntilAsync("2026-07-03", CancellationToken.None);

        result.ShouldBe("That is today (2026-07-03, Friday).");
    }

    [Fact]
    public async Task DaysUntil_Invalid_Returns_Hint()
    {
        var result = await Plugin().DaysUntilAsync("not-a-date", CancellationToken.None);

        result.ShouldBe(Scout.Plugin.Common.RelaxedDate.AcceptedFormatsHint());
    }

    [Fact]
    public async Task DayOfWeek_Reports_Weekday()
    {
        var result = await Plugin().DayOfWeekAsync("2026-09-15", CancellationToken.None);

        result.ShouldBe("2026-09-15 is a Tuesday.");
    }

    [Fact]
    public async Task DayOfWeek_Invalid_Returns_Hint()
    {
        var result = await Plugin().DayOfWeekAsync("13/13/2026", CancellationToken.None);

        result.ShouldBe(Scout.Plugin.Common.RelaxedDate.AcceptedFormatsHint());
    }

    [Fact]
    public async Task DateDifference_Reports_Weeks_And_Days()
    {
        var result = await Plugin()
            .DateDifferenceAsync("2026-07-03", "2026-08-26", CancellationToken.None);

        result.ShouldBe("54 days (7 weeks and 5 days).");
    }

    [Fact]
    public async Task DateDifference_Is_Order_Independent()
    {
        var forward = await Plugin()
            .DateDifferenceAsync("2026-07-03", "2026-08-26", CancellationToken.None);
        var backward = await Plugin()
            .DateDifferenceAsync("2026-08-26", "2026-07-03", CancellationToken.None);

        backward.ShouldBe(forward);
    }

    [Fact]
    public async Task DateDifference_Under_A_Week_Omits_Breakdown()
    {
        var result = await Plugin()
            .DateDifferenceAsync("2026-07-03", "2026-07-06", CancellationToken.None);

        result.ShouldBe("3 days.");
    }

    [Fact]
    public async Task DateDifference_Whole_Weeks_Omits_Trailing_Days()
    {
        var result = await Plugin()
            .DateDifferenceAsync("2026-07-03", "2026-07-17", CancellationToken.None);

        result.ShouldBe("14 days (2 weeks).");
    }

    [Fact]
    public async Task DateDifference_Invalid_Returns_Hint()
    {
        var result = await Plugin()
            .DateDifferenceAsync("2026-07-03", "nonsense", CancellationToken.None);

        result.ShouldBe(Scout.Plugin.Common.RelaxedDate.AcceptedFormatsHint());
    }

    [Fact]
    public async Task AddToDate_Adds_Days()
    {
        var result = await Plugin().AddToDateAsync("2026-07-03", "45", CancellationToken.None);

        result.ShouldBe("2026-07-03 + 45 days = Monday 2026-08-17.");
    }

    [Fact]
    public async Task AddToDate_Subtracts_Negative_Days()
    {
        var result = await Plugin().AddToDateAsync("2026-07-03", "-10", CancellationToken.None);

        result.ShouldBe("2026-07-03 - 10 days = Tuesday 2026-06-23.");
    }

    [Fact]
    public async Task AddToDate_Singular_Day()
    {
        var result = await Plugin().AddToDateAsync("2026-07-03", "1", CancellationToken.None);

        result.ShouldBe("2026-07-03 + 1 day = Saturday 2026-07-04.");
    }

    [Fact]
    public async Task AddToDate_Invalid_Date_Returns_Hint()
    {
        var result = await Plugin().AddToDateAsync("nope", "10", CancellationToken.None);

        result.ShouldBe(Scout.Plugin.Common.RelaxedDate.AcceptedFormatsHint());
    }

    [Fact]
    public async Task AddToDate_NonInteger_Days_Returns_Correction()
    {
        var result = await Plugin().AddToDateAsync("2026-07-03", "two", CancellationToken.None);

        result.ShouldBe("'two' is not a whole number of days. Use an integer like 45 or -10.");
    }

    [Fact]
    public async Task Cancellation_Returns_Clean_Error_Without_Throwing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var plugin = Plugin();
        string result = string.Empty;

        await Should.NotThrowAsync(async () =>
            result = await plugin.TodayAsync(string.Empty, cts.Token));

        result.ShouldBe("Date calculation was cancelled.");
    }

    [Fact]
    public async Task Overflow_Returns_Clean_Error_Without_Throwing()
    {
        var plugin = Plugin();
        string result = string.Empty;

        await Should.NotThrowAsync(async () =>
            result = await plugin.AddToDateAsync(
                "2026-07-03", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CancellationToken.None));

        result.ShouldBe("That date is outside the supported range.");
    }

    [Fact]
    public void Ctor_Throws_When_Clock_Null()
    {
        Should.Throw<ArgumentNullException>(() => new DatesPlugin(null!, London));
    }

    [Fact]
    public void Ctor_Throws_When_TimeZone_Blank()
    {
        Should.Throw<ArgumentException>(() => new DatesPlugin(new FixedTimeProvider(Now), "  "));
    }
}
