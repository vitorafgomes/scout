using System.Net;
using Scout.Plugin.Markets;
using Scout.Plugin.Markets.Tests.Fakes;

namespace Scout.Plugin.Markets.Tests;

/// <summary>
/// Networked tests for both external boundaries, driven over a stub
/// <see cref="HttpMessageHandler"/> so they never touch the network. Covers the
/// happy path plus the production-readiness failure cases for each source: timeout,
/// 5xx, malformed body, and source-specific empty/unknown/rate-limit conditions.
/// </summary>
public sealed class MarketsClientTests
{
    private static (StooqClient client, StubHttpMessageHandler handler) BuildStooq()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://stooq.com/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        return (new StooqClient(http), handler);
    }

    private static (CoinGeckoClient client, StubHttpMessageHandler handler) BuildCoinGecko()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.coingecko.com/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        // The real system clock keeps the short-TTL cache valid within a test and
        // never engages the throttle on a first call.
        return (new CoinGeckoClient(http, TimeProvider.System), handler);
    }

    // ----- Stooq boundary -----

    [Fact]
    public async Task Stooq_GetQuote_Sends_UserAgent_And_Expected_Path()
    {
        var (client, handler) = BuildStooq();
        handler.EnqueueBody(HttpStatusCode.OK,
            "AAPL.US,2024-01-05,22:00:04,181.99,182.76,180.17,181.18,62303020");

        await client.GetQuoteAsync("AAPL.US", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        var uri = req.RequestUri!.ToString();
        uri.ShouldContain("q/l/");
        uri.ShouldContain("s=AAPL.US");
        uri.ShouldContain("e=csv");
    }

    [Fact]
    public async Task Stooq_GetQuote_Parses_Csv_Line()
    {
        var (client, handler) = BuildStooq();
        handler.EnqueueBody(HttpStatusCode.OK,
            "PETR4.SA,2024-01-05,17:00:00,38.5,39.0,38.2,38.8,12345600");

        var quote = await client.GetQuoteAsync("PETR4.SA", CancellationToken.None);

        quote.ShouldNotBeNull();
        quote!.Symbol.ShouldBe("PETR4.SA");
        quote.Date.ShouldBe(new DateOnly(2024, 1, 5));
        quote.Time.ShouldBe("17:00:00");
        quote.Close.ShouldBe(38.8m);
        quote.Volume.ShouldBe(12345600L);
    }

    [Fact]
    public async Task Stooq_GetQuote_Skips_Header_Row()
    {
        var (client, handler) = BuildStooq();
        handler.EnqueueBody(HttpStatusCode.OK,
            "Symbol,Date,Time,Open,High,Low,Close,Volume\nAAPL.US,2024-01-05,22:00:04,181.99,182.76,180.17,181.18,62303020");

        var quote = await client.GetQuoteAsync("AAPL.US", CancellationToken.None);

        quote.ShouldNotBeNull();
        quote!.Close.ShouldBe(181.18m);
    }

    [Fact]
    public async Task Stooq_GetQuote_Unknown_Symbol_Returns_Null()
    {
        var (client, handler) = BuildStooq();
        // Stooq answers with "N/D" columns for an unknown ticker.
        handler.EnqueueBody(HttpStatusCode.OK, "ZZZZ.US,N/D,N/D,N/D,N/D,N/D,N/D,N/D");

        var quote = await client.GetQuoteAsync("ZZZZ.US", CancellationToken.None);

        quote.ShouldBeNull();
    }

    [Fact]
    public async Task Stooq_GetQuote_Empty_Body_Returns_Null()
    {
        var (client, handler) = BuildStooq();
        handler.EnqueueBody(HttpStatusCode.OK, string.Empty);

        var quote = await client.GetQuoteAsync("AAPL.US", CancellationToken.None);

        quote.ShouldBeNull();
    }

    [Fact]
    public async Task Stooq_GetQuote_Malformed_Csv_Throws_Unreadable()
    {
        var (client, handler) = BuildStooq();
        // Valid column count but a non-numeric close price.
        handler.EnqueueBody(HttpStatusCode.OK,
            "AAPL.US,2024-01-05,22:00:04,x,x,x,not-a-number,x");

        var ex = await Should.ThrowAsync<MarketsApiException>(
            () => client.GetQuoteAsync("AAPL.US", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Stooq_GetQuote_Truncated_Csv_Throws_Unreadable()
    {
        var (client, handler) = BuildStooq();
        handler.EnqueueBody(HttpStatusCode.OK, "AAPL.US,2024-01-05,22:00:04");

        var ex = await Should.ThrowAsync<MarketsApiException>(
            () => client.GetQuoteAsync("AAPL.US", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Stooq_GetQuote_ServerError_Throws_Unavailable()
    {
        var (client, handler) = BuildStooq();
        handler.EnqueueBody(HttpStatusCode.InternalServerError, "boom");

        var ex = await Should.ThrowAsync<MarketsApiException>(
            () => client.GetQuoteAsync("AAPL.US", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Stooq_GetQuote_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = BuildStooq();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetQuoteAsync("AAPL.US", CancellationToken.None));
    }

    [Fact]
    public async Task Stooq_GetQuote_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = BuildStooq();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetQuoteAsync("AAPL.US", CancellationToken.None));
    }

    // ----- CoinGecko boundary -----

    [Fact]
    public async Task CoinGecko_GetPrice_Sends_UserAgent_And_Expected_Path()
    {
        var (client, handler) = BuildCoinGecko();
        handler.EnqueueBody(HttpStatusCode.OK,
            """{"bitcoin":{"usd":63000.5,"usd_24h_change":1.23}}""");

        await client.GetPriceAsync("bitcoin", "usd", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        var uri = req.RequestUri!.ToString();
        uri.ShouldContain("api/v3/simple/price");
        uri.ShouldContain("ids=bitcoin");
        uri.ShouldContain("vs_currencies=usd");
    }

    [Fact]
    public async Task CoinGecko_GetPrice_Parses_Price_And_Change()
    {
        var (client, handler) = BuildCoinGecko();
        handler.EnqueueBody(HttpStatusCode.OK,
            """{"bitcoin":{"usd":63000.5,"usd_24h_change":1.23}}""");

        var price = await client.GetPriceAsync("bitcoin", "usd", CancellationToken.None);

        price.ShouldNotBeNull();
        price!.Coin.ShouldBe("bitcoin");
        price.Currency.ShouldBe("usd");
        price.Price.ShouldBe(63000.5m);
        price.Change24h.ShouldBe(1.23m);
    }

    [Fact]
    public async Task CoinGecko_GetPrice_Unknown_Coin_Returns_Null()
    {
        var (client, handler) = BuildCoinGecko();
        // CoinGecko answers 200 with an empty object for an unknown id.
        handler.EnqueueBody(HttpStatusCode.OK, "{}");

        var price = await client.GetPriceAsync("not-a-coin", "usd", CancellationToken.None);

        price.ShouldBeNull();
    }

    [Fact]
    public async Task CoinGecko_GetPrice_Caches_Repeated_Lookup()
    {
        var (client, handler) = BuildCoinGecko();
        handler.EnqueueBody(HttpStatusCode.OK,
            """{"bitcoin":{"usd":63000.5,"usd_24h_change":1.23}}""");

        var first = await client.GetPriceAsync("bitcoin", "usd", CancellationToken.None);
        var second = await client.GetPriceAsync("bitcoin", "usd", CancellationToken.None);

        // Only one response was enqueued: a second HTTP call would throw.
        handler.RequestCount.ShouldBe(1);
        second.ShouldBe(first);
    }

    [Fact]
    public async Task CoinGecko_GetTrending_Parses_And_Uses_Expected_Path()
    {
        var (client, handler) = BuildCoinGecko();
        handler.EnqueueBody(HttpStatusCode.OK,
            """
            {"coins":[
              {"item":{"id":"pepe","name":"Pepe","symbol":"pepe","market_cap_rank":45}},
              {"item":{"id":"solana","name":"Solana","symbol":"sol","market_cap_rank":5}}
            ]}
            """);

        var coins = await client.GetTrendingAsync(CancellationToken.None);

        coins.Count.ShouldBe(2);
        coins[0].Id.ShouldBe("pepe");
        coins[0].Name.ShouldBe("Pepe");
        coins[0].Symbol.ShouldBe("PEPE");
        coins[0].MarketCapRank.ShouldBe(45);
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("api/v3/search/trending");
    }

    [Fact]
    public async Task CoinGecko_GetPrice_RateLimited_Throws_RateLimit_Message()
    {
        var (client, handler) = BuildCoinGecko();
        handler.EnqueueBody(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Should.ThrowAsync<MarketsApiException>(
            () => client.GetPriceAsync("bitcoin", "usd", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.Message.ShouldContain("rate limiting");
    }

    [Fact]
    public async Task CoinGecko_GetPrice_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = BuildCoinGecko();
        handler.EnqueueBody(HttpStatusCode.ServiceUnavailable, "{}");

        var ex = await Should.ThrowAsync<MarketsApiException>(
            () => client.GetPriceAsync("bitcoin", "usd", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task CoinGecko_GetPrice_Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = BuildCoinGecko();
        handler.EnqueueBody(HttpStatusCode.OK, "this is not json{");

        var ex = await Should.ThrowAsync<MarketsApiException>(
            () => client.GetPriceAsync("bitcoin", "usd", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task CoinGecko_GetPrice_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = BuildCoinGecko();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetPriceAsync("bitcoin", "usd", CancellationToken.None));
    }

    [Fact]
    public async Task CoinGecko_GetTrending_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = BuildCoinGecko();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetTrendingAsync(CancellationToken.None));
    }
}
