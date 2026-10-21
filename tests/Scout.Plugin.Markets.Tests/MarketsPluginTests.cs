using Scout.Plugin.Markets;
using Scout.Plugin.Markets.Abstractions;

namespace Scout.Plugin.Markets.Tests;

public sealed class MarketsPluginTests
{
    private static IStockQuoteClient Stocks() => Substitute.For<IStockQuoteClient>();

    private static ICryptoMarketClient Crypto() => Substitute.For<ICryptoMarketClient>();

    private static MarketsPlugin Plugin(IStockQuoteClient stocks, ICryptoMarketClient crypto)
        => new(stocks, crypto);

    private static StockQuote Quote(decimal open, decimal close)
        => new("AAPL.US", new DateOnly(2024, 1, 5), "22:00:04", open, close + 1m, open - 1m, close, 1000);

    // ----- get_stock_quote -----

    [Fact]
    public async Task GetStockQuote_Formats_Delayed_Quote_With_Move()
    {
        var stocks = Stocks();
        stocks.GetQuoteAsync("AAPL.US", Arg.Any<CancellationToken>())
            .Returns(Quote(open: 100m, close: 105m));

        var result = await Plugin(stocks, Crypto())
            .GetStockQuoteAsync("aapl.us", CancellationToken.None);

        result.ShouldContain("AAPL.US: 105");
        result.ShouldContain("+5");
        result.ShouldContain("delayed quote");
        result.ShouldContain("2024-01-05");
        await stocks.Received(1).GetQuoteAsync("AAPL.US", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStockQuote_Unknown_Symbol_Returns_Not_Found()
    {
        var stocks = Stocks();
        stocks.GetQuoteAsync("AAPL.US", Arg.Any<CancellationToken>())
            .Returns((StockQuote?)null);

        var result = await Plugin(stocks, Crypto())
            .GetStockQuoteAsync("AAPL.US", CancellationToken.None);

        result.ShouldContain("No quote found for AAPL.US");
    }

    [Fact]
    public async Task GetStockQuote_Invalid_Symbol_Returns_Teaching()
    {
        var stocks = Stocks();

        var result = await Plugin(stocks, Crypto())
            .GetStockQuoteAsync("  ", CancellationToken.None);

        result.ShouldBe("Provide a ticker symbol like AAPL.US or PETR4.SA.");
        await stocks.DidNotReceiveWithAnyArgs().GetQuoteAsync(default!, default);
    }

    [Fact]
    public async Task GetStockQuote_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var stocks = Stocks();
        stocks.GetQuoteAsync("AAPL.US", Arg.Any<CancellationToken>())
            .Returns<StockQuote?>(_ => throw new MarketsApiException(
                "The market data service is currently unavailable. Try again later."));

        var plugin = Plugin(stocks, Crypto());

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetStockQuoteAsync("AAPL.US", CancellationToken.None));

        result.ShouldBe("The market data service is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task GetStockQuote_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var stocks = Stocks();
        stocks.GetQuoteAsync("AAPL.US", Arg.Any<CancellationToken>())
            .Returns<StockQuote?>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(stocks, Crypto());

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetStockQuoteAsync("AAPL.US", CancellationToken.None));

        result.ShouldBe("The stock quote request timed out. Try again later.");
    }

    [Fact]
    public async Task GetStockQuote_Transport_Failure_Returns_Clean_Error_Without_Throwing()
    {
        var stocks = Stocks();
        stocks.GetQuoteAsync("AAPL.US", Arg.Any<CancellationToken>())
            .Returns<StockQuote?>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(stocks, Crypto());

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetStockQuoteAsync("AAPL.US", CancellationToken.None));

        result.ShouldBe("Could not reach the stock quote service. Try again later.");
    }

    // ----- get_crypto_price -----

    [Fact]
    public async Task GetCryptoPrice_Formats_Delayed_Price_With_Change()
    {
        var crypto = Crypto();
        crypto.GetPriceAsync("bitcoin", "usd", Arg.Any<CancellationToken>())
            .Returns(new CryptoPrice("bitcoin", "usd", 63000.5m, 1.23m));

        var result = await Plugin(Stocks(), crypto)
            .GetCryptoPriceAsync("BITCOIN", "usd", CancellationToken.None);

        result.ShouldContain("bitcoin = 63000.5 USD");
        result.ShouldContain("+1.23% 24h");
        result.ShouldContain("delayed quote");
        await crypto.Received(1).GetPriceAsync("bitcoin", "usd", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCryptoPrice_Defaults_Currency_To_Usd()
    {
        var crypto = Crypto();
        crypto.GetPriceAsync("ethereum", "usd", Arg.Any<CancellationToken>())
            .Returns(new CryptoPrice("ethereum", "usd", 3200m, null));

        var result = await Plugin(Stocks(), crypto)
            .GetCryptoPriceAsync("ethereum");

        result.ShouldContain("ethereum = 3200 USD");
        result.ShouldContain("delayed quote");
        await crypto.Received(1).GetPriceAsync("ethereum", "usd", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCryptoPrice_Unknown_Coin_Returns_Not_Found()
    {
        var crypto = Crypto();
        crypto.GetPriceAsync("bitcoin", "usd", Arg.Any<CancellationToken>())
            .Returns((CryptoPrice?)null);

        var result = await Plugin(Stocks(), crypto)
            .GetCryptoPriceAsync("bitcoin", "usd", CancellationToken.None);

        result.ShouldContain("No price found for bitcoin");
    }

    [Fact]
    public async Task GetCryptoPrice_Invalid_Coin_Returns_Teaching()
    {
        var crypto = Crypto();

        var result = await Plugin(Stocks(), crypto)
            .GetCryptoPriceAsync("", "usd", CancellationToken.None);

        result.ShouldBe("Provide a coin id like bitcoin or ethereum.");
        await crypto.DidNotReceiveWithAnyArgs().GetPriceAsync(default!, default!, default);
    }

    [Fact]
    public async Task GetCryptoPrice_Invalid_Currency_Returns_Teaching()
    {
        var crypto = Crypto();

        var result = await Plugin(Stocks(), crypto)
            .GetCryptoPriceAsync("bitcoin", "12", CancellationToken.None);

        result.ShouldBe("Use a currency code like usd, eur, or brl.");
        await crypto.DidNotReceiveWithAnyArgs().GetPriceAsync(default!, default!, default);
    }

    [Fact]
    public async Task GetCryptoPrice_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var crypto = Crypto();
        crypto.GetPriceAsync("bitcoin", "usd", Arg.Any<CancellationToken>())
            .Returns<CryptoPrice?>(_ => throw new MarketsApiException(
                "The crypto price service is rate limiting requests. Try again shortly."));

        var plugin = Plugin(Stocks(), crypto);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetCryptoPriceAsync("bitcoin", "usd", CancellationToken.None));

        result.ShouldBe("The crypto price service is rate limiting requests. Try again shortly.");
    }

    // ----- get_trending_crypto -----

    [Fact]
    public async Task GetTrendingCrypto_Lists_Coins_One_Per_Line()
    {
        var crypto = Crypto();
        crypto.GetTrendingAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TrendingCoin>
            {
                new("pepe", "Pepe", "PEPE", 45),
                new("solana", "Solana", "SOL", 5),
            });

        var result = await Plugin(Stocks(), crypto)
            .GetTrendingCryptoAsync(CancellationToken.None);

        var lines = result.Split('\n');
        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("1. Pepe (PEPE) - market cap rank 45");
        lines[1].ShouldBe("2. Solana (SOL) - market cap rank 5");
    }

    [Fact]
    public async Task GetTrendingCrypto_Empty_Returns_Message()
    {
        var crypto = Crypto();
        crypto.GetTrendingAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TrendingCoin>());

        var result = await Plugin(Stocks(), crypto)
            .GetTrendingCryptoAsync(CancellationToken.None);

        result.ShouldBe("No trending cryptocurrencies are available right now.");
    }

    [Fact]
    public async Task GetTrendingCrypto_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var crypto = Crypto();
        crypto.GetTrendingAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TrendingCoin>>(_ => throw new HttpRequestException("boom"));

        var plugin = Plugin(Stocks(), crypto);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetTrendingCryptoAsync(CancellationToken.None));

        result.ShouldBe("Could not reach the trending crypto service. Try again later.");
    }

    // ----- construction guards -----

    [Fact]
    public void Ctor_Throws_When_Stocks_Null()
    {
        Should.Throw<ArgumentNullException>(() => new MarketsPlugin(null!, Crypto()));
    }

    [Fact]
    public void Ctor_Throws_When_Crypto_Null()
    {
        Should.Throw<ArgumentNullException>(() => new MarketsPlugin(Stocks(), null!));
    }
}
