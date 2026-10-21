using System.Net;
using Scout.Plugin.Holidays;
using Scout.Plugin.Holidays.Tests.Fakes;

namespace Scout.Plugin.Holidays.Tests;

public sealed class HolidaysClientTests
{
    private static (HolidaysClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://date.nager.at/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return (new HolidaysClient(http), handler);
    }

    [Fact]
    public async Task GetNextHolidays_Sends_Expected_Headers_And_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "[]");

        await client.GetNextHolidaysAsync("GB", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.RequestUri!.ToString().ShouldContain("api/v3/NextPublicHolidays/GB");
    }

    [Fact]
    public async Task GetNextHolidays_Parses_Items()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """[{"date":"2026-12-25","localName":"Christmas Day","name":"Christmas Day","countryCode":"GB"}]""");

        var holidays = await client.GetNextHolidaysAsync("GB", CancellationToken.None);

        holidays.Count.ShouldBe(1);
        holidays[0].Date.ShouldBe(new DateOnly(2026, 12, 25));
        holidays[0].Name.ShouldBe("Christmas Day");
        holidays[0].LocalName.ShouldBe("Christmas Day");
    }

    [Fact]
    public async Task GetHolidaysForYear_Parses_Items_And_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """[{"date":"2026-01-01","localName":"Ano Novo","name":"New Year's Day","countryCode":"BR"}]""");

        var holidays = await client.GetHolidaysForYearAsync(2026, "BR", CancellationToken.None);

        holidays.Count.ShouldBe(1);
        holidays[0].Date.ShouldBe(new DateOnly(2026, 1, 1));
        holidays[0].LocalName.ShouldBe("Ano Novo");
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("api/v3/PublicHolidays/2026/BR");
    }

    [Fact]
    public async Task GetLongWeekends_Parses_Items_And_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """[{"startDate":"2026-05-02","endDate":"2026-05-04","dayCount":3,"needBridgeDay":true}]""");

        var weekends = await client.GetLongWeekendsAsync(2026, "GB", CancellationToken.None);

        weekends.Count.ShouldBe(1);
        weekends[0].StartDate.ShouldBe(new DateOnly(2026, 5, 2));
        weekends[0].EndDate.ShouldBe(new DateOnly(2026, 5, 4));
        weekends[0].DayCount.ShouldBe(3);
        weekends[0].NeedBridgeDay.ShouldBeTrue();
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("api/v3/LongWeekend/2026/GB");
    }

    [Fact]
    public async Task Empty_Array_Returns_Empty_List()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "[]");

        var holidays = await client.GetHolidaysForYearAsync(2026, "GB", CancellationToken.None);

        holidays.ShouldBeEmpty();
    }

    [Fact]
    public async Task NotFound_Throws_Unknown_Country_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        var ex = await Should.ThrowAsync<HolidaysApiException>(
            () => client.GetNextHolidaysAsync("ZZ", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ex.Message.ShouldContain("ISO codes");
    }

    [Fact]
    public async Task RateLimited_Throws_RateLimit_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Should.ThrowAsync<HolidaysApiException>(
            () => client.GetNextHolidaysAsync("GB", CancellationToken.None));

        ex.Message.ShouldContain("rate limiting");
    }

    [Fact]
    public async Task ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<HolidaysApiException>(
            () => client.GetHolidaysForYearAsync(2026, "GB", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{ this is not valid json");

        var ex = await Should.ThrowAsync<HolidaysApiException>(
            () => client.GetNextHolidaysAsync("GB", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetNextHolidaysAsync("GB", CancellationToken.None));
    }

    [Fact]
    public async Task Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetNextHolidaysAsync("GB", CancellationToken.None));
    }
}
