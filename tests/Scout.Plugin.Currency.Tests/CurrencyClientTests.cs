using System.Net;
using Scout.Plugin.Currency;
using Scout.Plugin.Currency.Tests.Fakes;

namespace Scout.Plugin.Currency.Tests;

public sealed class CurrencyClientTests
{
    private static (CurrencyClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.frankfurter.dev/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        return (new CurrencyClient(http), handler);
    }

    [Fact]
    public async Task GetLatestRate_Sends_UserAgent_And_Expected_Url()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"amount":1.0,"base":"GBP","date":"2024-01-31","rates":{"BRL":6.1234}}""");

        await client.GetLatestRateAsync("GBP", "BRL", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        var uri = req.RequestUri!.ToString();
        uri.ShouldContain("v1/latest");
        uri.ShouldContain("base=GBP");
        uri.ShouldContain("symbols=BRL");
    }

    [Fact]
    public async Task GetLatestRate_Parses_Rate_And_Date()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"amount":1.0,"base":"GBP","date":"2024-01-31","rates":{"BRL":6.1234}}""");

        var rate = await client.GetLatestRateAsync("GBP", "BRL", CancellationToken.None);

        rate.ShouldNotBeNull();
        rate!.Base.ShouldBe("GBP");
        rate.Target.ShouldBe("BRL");
        rate.Rate.ShouldBe(6.1234m);
        rate.Date.ShouldBe(new DateOnly(2024, 1, 31));
    }

    [Fact]
    public async Task GetLatestRate_Unknown_Symbol_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"amount":1.0,"base":"GBP","date":"2024-01-31","rates":{}}""");

        var rate = await client.GetLatestRateAsync("GBP", "ZZZ", CancellationToken.None);

        rate.ShouldBeNull();
    }

    [Fact]
    public async Task GetLatestRate_NotFound_Returns_Null()
    {
        var (client, handler) = Build();
        // Frankfurter answers 404 for an unknown currency; the client treats it
        // as "no rate" rather than an error.
        handler.EnqueueJson(HttpStatusCode.NotFound, """{"message":"not found"}""");

        var rate = await client.GetLatestRateAsync("GBP", "ZZZ", CancellationToken.None);

        rate.ShouldBeNull();
    }

    [Fact]
    public async Task GetLatestRate_RateLimited_Throws_RateLimit_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Should.ThrowAsync<CurrencyApiException>(
            () => client.GetLatestRateAsync("GBP", "BRL", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.Message.ShouldContain("rate limit");
    }

    [Fact]
    public async Task GetLatestRate_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<CurrencyApiException>(
            () => client.GetLatestRateAsync("GBP", "BRL", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task GetLatestRate_Malformed_Body_Throws_CurrencyApiException()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "this is not json");

        var ex = await Should.ThrowAsync<CurrencyApiException>(
            () => client.GetLatestRateAsync("GBP", "BRL", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task GetLatestRate_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetLatestRateAsync("GBP", "BRL", CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestRate_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetLatestRateAsync("GBP", "BRL", CancellationToken.None));
    }

    [Fact]
    public async Task GetTrend_Parses_Series_And_Computes_Low_High_First_Last()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {
              "amount": 1.0,
              "base": "GBP",
              "start_date": "2024-01-02",
              "end_date": "2024-01-04",
              "rates": {
                "2024-01-02": {"BRL": 6.1},
                "2024-01-03": {"BRL": 6.3},
                "2024-01-04": {"BRL": 5.9}
              }
            }
            """);

        var trend = await client.GetTrendAsync("GBP", "BRL", CancellationToken.None);

        trend.ShouldNotBeNull();
        trend!.Base.ShouldBe("GBP");
        trend.Target.ShouldBe("BRL");
        trend.Low.ShouldBe(5.9m);
        trend.High.ShouldBe(6.3m);
        trend.First.ShouldBe(6.1m);
        trend.Last.ShouldBe(5.9m);
        trend.StartDate.ShouldBe(new DateOnly(2024, 1, 2));
        trend.EndDate.ShouldBe(new DateOnly(2024, 1, 4));
    }

    [Fact]
    public async Task GetTrend_Uses_Range_Url()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"amount":1.0,"base":"GBP","rates":{"2024-01-02":{"BRL":6.1}}}""");

        await client.GetTrendAsync("GBP", "BRL", CancellationToken.None);

        var uri = handler.LastRequest!.RequestUri!.ToString();
        uri.ShouldContain("v1/");
        uri.ShouldContain("..");
        uri.ShouldContain("base=GBP");
        uri.ShouldContain("symbols=BRL");
    }

    [Fact]
    public async Task GetTrend_Empty_Series_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"amount":1.0,"base":"GBP","rates":{}}""");

        var trend = await client.GetTrendAsync("GBP", "BRL", CancellationToken.None);

        trend.ShouldBeNull();
    }

    [Fact]
    public async Task GetTrend_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "{}");

        var ex = await Should.ThrowAsync<CurrencyApiException>(
            () => client.GetTrendAsync("GBP", "BRL", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }
}
