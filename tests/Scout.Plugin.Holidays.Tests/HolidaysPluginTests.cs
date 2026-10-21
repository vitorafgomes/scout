using Microsoft.Extensions.Time.Testing;
using Scout.Plugin.Holidays;
using Scout.Plugin.Holidays.Abstractions;

namespace Scout.Plugin.Holidays.Tests;

public sealed class HolidaysPluginTests
{
    private static IHolidaysClient Client() => Substitute.For<IHolidaysClient>();

    private static FakeTimeProvider ClockAt(int year, int month, int day)
        => new(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

    // Fix the time zone to UTC so relative-date resolution is deterministic and
    // free of daylight-saving edge cases in these tests.
    private static HolidaysOptions Options() => new()
    {
        DefaultCountry = "GB",
        TimeZone = "UTC",
    };

    private static HolidaysPlugin Plugin(IHolidaysClient client, TimeProvider clock)
        => new(client, clock, Options());

    private static PublicHoliday Holiday(string date, string name)
        => new(DateOnly.ParseExact(date, "yyyy-MM-dd"), name, name);

    [Fact]
    public async Task GetNextHolidays_Formats_Only_First_Three()
    {
        var client = Client();
        client.GetNextHolidaysAsync("GB", Arg.Any<CancellationToken>())
            .Returns(new List<PublicHoliday>
            {
                Holiday("2026-06-02", "First"),
                Holiday("2026-07-03", "Second"),
                Holiday("2026-08-04", "Third"),
                Holiday("2026-09-05", "Fourth"),
            });

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .GetNextHolidaysAsync("GB", CancellationToken.None);

        result.ShouldContain("First");
        result.ShouldContain("Second");
        result.ShouldContain("Third");
        result.ShouldNotContain("Fourth");
        result.ShouldContain("Tuesday 2026-06-02");
        result.ShouldContain("in 1 day");
    }

    [Fact]
    public async Task GetNextHolidays_Uppercases_Country()
    {
        var client = Client();
        client.GetNextHolidaysAsync("BR", Arg.Any<CancellationToken>())
            .Returns(new List<PublicHoliday> { Holiday("2026-09-07", "Independencia") });

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .GetNextHolidaysAsync("br", CancellationToken.None);

        result.ShouldContain("Independencia");
        await client.Received(1).GetNextHolidaysAsync("BR", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetNextHolidays_Empty_Returns_Message()
    {
        var client = Client();
        client.GetNextHolidaysAsync("GB", Arg.Any<CancellationToken>())
            .Returns(new List<PublicHoliday>());

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .GetNextHolidaysAsync("GB", CancellationToken.None);

        result.ShouldBe("No upcoming public holidays found for GB.");
    }

    [Fact]
    public async Task GetNextHolidays_Invalid_Country_Returns_Teaching()
    {
        var client = Client();

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .GetNextHolidaysAsync("portugal", CancellationToken.None);

        result.ShouldBe("Use ISO codes like BR, GB, PT.");
        await client.DidNotReceiveWithAnyArgs().GetNextHolidaysAsync(default!, default);
    }

    [Fact]
    public async Task IsHoliday_Yes_Names_The_Holiday()
    {
        var client = Client();
        client.GetHolidaysForYearAsync(2026, "GB", Arg.Any<CancellationToken>())
            .Returns(new List<PublicHoliday> { Holiday("2026-12-25", "Christmas Day") });

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .IsHolidayAsync("2026-12-25", "GB", CancellationToken.None);

        result.ShouldContain("Yes");
        result.ShouldContain("Christmas Day");
        result.ShouldContain("2026-12-25");
    }

    [Fact]
    public async Task IsHoliday_No_Returns_Negative_Sentence()
    {
        var client = Client();
        client.GetHolidaysForYearAsync(2026, "GB", Arg.Any<CancellationToken>())
            .Returns(new List<PublicHoliday> { Holiday("2026-12-25", "Christmas Day") });

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .IsHolidayAsync("2026-12-24", "GB", CancellationToken.None);

        result.ShouldBe("No - 2026-12-24 is not a public holiday in GB.");
    }

    [Fact]
    public async Task IsHoliday_Accepts_Relative_Today()
    {
        var client = Client();
        client.GetHolidaysForYearAsync(2026, "GB", Arg.Any<CancellationToken>())
            .Returns(new List<PublicHoliday> { Holiday("2026-12-25", "Christmas Day") });

        var result = await Plugin(client, ClockAt(2026, 12, 25))
            .IsHolidayAsync("today", "GB", CancellationToken.None);

        result.ShouldContain("Yes");
        result.ShouldContain("Christmas Day");
    }

    [Fact]
    public async Task IsHoliday_Invalid_Date_Returns_Hint()
    {
        var client = Client();

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .IsHolidayAsync("not-a-date", "GB", CancellationToken.None);

        result.ShouldBe(Scout.Plugin.Common.RelaxedDate.AcceptedFormatsHint());
        await client.DidNotReceiveWithAnyArgs().GetHolidaysForYearAsync(default, default!, default);
    }

    [Fact]
    public async Task ListHolidays_Formats_One_Per_Line()
    {
        var client = Client();
        client.GetHolidaysForYearAsync(2026, "GB", Arg.Any<CancellationToken>())
            .Returns(new List<PublicHoliday>
            {
                Holiday("2026-01-01", "New Year's Day"),
                Holiday("2026-12-25", "Christmas Day"),
            });

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .ListHolidaysAsync("GB", "2026", CancellationToken.None);

        var lines = result.Split('\n');
        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("2026-01-01 Thursday New Year's Day");
        lines[1].ShouldBe("2026-12-25 Friday Christmas Day");
    }

    [Fact]
    public async Task ListHolidays_Empty_Returns_Message()
    {
        var client = Client();
        client.GetHolidaysForYearAsync(2026, "GB", Arg.Any<CancellationToken>())
            .Returns(new List<PublicHoliday>());

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .ListHolidaysAsync("GB", "2026", CancellationToken.None);

        result.ShouldBe("No public holidays found for GB in 2026.");
    }

    [Fact]
    public async Task ListHolidays_Invalid_Country_Returns_Teaching()
    {
        var client = Client();

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .ListHolidaysAsync("portugal", "2026", CancellationToken.None);

        result.ShouldBe("Use ISO codes like BR, GB, PT.");
    }

    [Fact]
    public async Task ListHolidays_Invalid_Year_Returns_Teaching()
    {
        var client = Client();

        var result = await Plugin(client, ClockAt(2026, 6, 1))
            .ListHolidaysAsync("GB", "abcd", CancellationToken.None);

        result.ShouldBe("Provide a 4-digit year like 2026.");
    }

    [Fact]
    public async Task GetLongWeekends_Returns_Only_Remaining()
    {
        var client = Client();
        client.GetLongWeekendsAsync(2026, "GB", Arg.Any<CancellationToken>())
            .Returns(new List<LongWeekend>
            {
                new(new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 4), 3, false),
                new(new DateOnly(2026, 5, 2), new DateOnly(2026, 5, 4), 3, true),
            });

        var result = await Plugin(client, ClockAt(2026, 4, 1))
            .GetLongWeekendsAsync("GB", CancellationToken.None);

        result.ShouldNotContain("2026-01-02");
        result.ShouldContain("2026-05-02 to 2026-05-04 (3 days)");
        result.ShouldContain("bridge day needed");
    }

    [Fact]
    public async Task GetLongWeekends_Empty_Returns_Message()
    {
        var client = Client();
        client.GetLongWeekendsAsync(2026, "GB", Arg.Any<CancellationToken>())
            .Returns(new List<LongWeekend>());

        var result = await Plugin(client, ClockAt(2026, 4, 1))
            .GetLongWeekendsAsync("GB", CancellationToken.None);

        result.ShouldBe("No remaining long weekends this year for GB.");
    }

    [Fact]
    public async Task GetNextHolidays_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetNextHolidaysAsync("GB", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<PublicHoliday>>(
                _ => throw new HolidaysApiException("The holidays service is currently unavailable. Try again later."));

        var plugin = Plugin(client, ClockAt(2026, 6, 1));

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetNextHolidaysAsync("GB", CancellationToken.None));

        result.ShouldBe("The holidays service is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task IsHoliday_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetHolidaysForYearAsync(2026, "GB", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<PublicHoliday>>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(client, ClockAt(2026, 6, 1));

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.IsHolidayAsync("2026-12-25", "GB", CancellationToken.None));

        result.ShouldBe("The holidays request timed out. Try again later.");
    }

    [Fact]
    public async Task ListHolidays_HttpRequestException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetHolidaysForYearAsync(2026, "GB", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<PublicHoliday>>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client, ClockAt(2026, 6, 1));

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.ListHolidaysAsync("GB", "2026", CancellationToken.None));

        result.ShouldBe("Could not reach the holidays service. Try again later.");
    }

    [Fact]
    public async Task GetLongWeekends_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetLongWeekendsAsync(2026, "GB", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<LongWeekend>>(
                _ => throw new HolidaysApiException("Unknown country code. Use ISO codes like BR, GB, PT."));

        var plugin = Plugin(client, ClockAt(2026, 4, 1));

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetLongWeekendsAsync("GB", CancellationToken.None));

        result.ShouldBe("Unknown country code. Use ISO codes like BR, GB, PT.");
    }

    [Fact]
    public void Ctor_Throws_When_Client_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new HolidaysPlugin(null!, ClockAt(2026, 6, 1), Options()));
    }

    [Fact]
    public void Ctor_Throws_When_Clock_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new HolidaysPlugin(Client(), null!, Options()));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new HolidaysPlugin(Client(), ClockAt(2026, 6, 1), null!));
    }
}
