using Scout.Plugin.Currency;
using Scout.Plugin.Currency.Abstractions;

namespace Scout.Plugin.Currency.Tests;

public sealed class CurrencyPluginTests
{
    private static readonly DateOnly SampleDate = new(2024, 1, 31);

    private static ICurrencyClient Client() => Substitute.For<ICurrencyClient>();

    private static CurrencyPlugin Plugin(ICurrencyClient client) => new(client);

    private static ExchangeRate Rate(decimal value)
        => new("GBP", "BRL", value, SampleDate);

    [Fact]
    public async Task GetExchangeRate_Formats_Rate_Line()
    {
        var client = Client();
        client.GetLatestRateAsync("GBP", "BRL", Arg.Any<CancellationToken>())
            .Returns(Rate(6.1234m));

        var result = await Plugin(client).GetExchangeRateAsync("gbp", "brl", CancellationToken.None);

        result.ShouldBe("1 GBP = 6.1234 BRL (ECB 2024-01-31)");
    }

    [Fact]
    public async Task GetExchangeRate_Null_Returns_Message()
    {
        var client = Client();
        client.GetLatestRateAsync("GBP", "ZZZ", Arg.Any<CancellationToken>())
            .Returns((ExchangeRate?)null);

        var result = await Plugin(client).GetExchangeRateAsync("GBP", "ZZZ", CancellationToken.None);

        result.ShouldBe("No ECB rate available for GBP to ZZZ.");
    }

    [Fact]
    public async Task GetExchangeRate_Invalid_Code_Returns_Corrective_Sentence()
    {
        var result = await Plugin(Client())
            .GetExchangeRateAsync("pounds", "BRL", CancellationToken.None);

        result.ShouldContain("Use ISO codes like GBP, BRL, EUR");
    }

    [Fact]
    public async Task ConvertCurrency_Formats_Converted_Amount_And_Rate()
    {
        var client = Client();
        client.GetLatestRateAsync("GBP", "BRL", Arg.Any<CancellationToken>())
            .Returns(Rate(6.1234m));

        var result = await Plugin(client)
            .ConvertCurrencyAsync("250", "GBP", "BRL", CancellationToken.None);

        result.ShouldBe(
            "250.00 GBP = 1530.85 BRL (rate 1 GBP = 6.1234 BRL, ECB 2024-01-31)");
    }

    [Theory]
    [InlineData("250", "1500.00")]
    [InlineData("250.5", "1503.00")]
    [InlineData("250,5", "1503.00")]
    [InlineData("£250", "1500.00")]
    [InlineData("R$ 120", "720.00")]
    [InlineData("1,250.50", "7503.00")]
    public async Task ConvertCurrency_Parses_Amount_Defensively(string amount, string expectedConverted)
    {
        var client = Client();
        client.GetLatestRateAsync("GBP", "BRL", Arg.Any<CancellationToken>())
            .Returns(Rate(6m));

        var result = await Plugin(client)
            .ConvertCurrencyAsync(amount, "GBP", "BRL", CancellationToken.None);

        result.ShouldContain($"= {expectedConverted} BRL");
    }

    [Fact]
    public async Task ConvertCurrency_Invalid_Amount_Returns_Corrective_Sentence()
    {
        var client = Client();
        client.GetLatestRateAsync("GBP", "BRL", Arg.Any<CancellationToken>())
            .Returns(Rate(6m));

        var result = await Plugin(client)
            .ConvertCurrencyAsync("not a number", "GBP", "BRL", CancellationToken.None);

        result.ShouldBe("Could not read the amount. Provide a positive number like 250 or 250.50.");
    }

    [Fact]
    public async Task ConvertCurrency_Invalid_Code_Returns_Corrective_Sentence()
    {
        var result = await Plugin(Client())
            .ConvertCurrencyAsync("250", "GBP", "reais", CancellationToken.None);

        result.ShouldContain("Use ISO codes like GBP, BRL, EUR");
    }

    [Fact]
    public async Task ConvertCurrency_Null_Rate_Returns_Message()
    {
        var client = Client();
        client.GetLatestRateAsync("GBP", "ZZZ", Arg.Any<CancellationToken>())
            .Returns((ExchangeRate?)null);

        var result = await Plugin(client)
            .ConvertCurrencyAsync("250", "GBP", "ZZZ", CancellationToken.None);

        result.ShouldBe("No ECB rate available for GBP to ZZZ.");
    }

    [Fact]
    public async Task GetRateTrend_Strengthened_When_Last_Above_First()
    {
        var client = Client();
        client.GetTrendAsync("GBP", "BRL", Arg.Any<CancellationToken>())
            .Returns(new RateTrend("GBP", "BRL",
                Low: 5.5m, High: 6.5m, First: 6.0m, Last: 6.2m,
                StartDate: new DateOnly(2024, 1, 1), EndDate: new DateOnly(2024, 1, 31)));

        var result = await Plugin(client).GetRateTrendAsync("GBP", "BRL", CancellationToken.None);

        result.ShouldContain("low 5.5");
        result.ShouldContain("high 6.5");
        result.ShouldContain("GBP strengthened against BRL");
        result.ShouldContain("from 6.0 to 6.2");
    }

    [Fact]
    public async Task GetRateTrend_Weakened_When_Last_Below_First()
    {
        var client = Client();
        client.GetTrendAsync("GBP", "BRL", Arg.Any<CancellationToken>())
            .Returns(new RateTrend("GBP", "BRL",
                Low: 5.5m, High: 6.5m, First: 6.2m, Last: 6.0m,
                StartDate: new DateOnly(2024, 1, 1), EndDate: new DateOnly(2024, 1, 31)));

        var result = await Plugin(client).GetRateTrendAsync("GBP", "BRL", CancellationToken.None);

        result.ShouldContain("GBP weakened against BRL");
    }

    [Fact]
    public async Task GetRateTrend_Null_Returns_Message()
    {
        var client = Client();
        client.GetTrendAsync("GBP", "ZZZ", Arg.Any<CancellationToken>())
            .Returns((RateTrend?)null);

        var result = await Plugin(client).GetRateTrendAsync("GBP", "ZZZ", CancellationToken.None);

        result.ShouldBe("No ECB rate history available for GBP to ZZZ.");
    }

    [Fact]
    public async Task GetRateTrend_Invalid_Code_Returns_Corrective_Sentence()
    {
        var result = await Plugin(Client())
            .GetRateTrendAsync("US", "BRL", CancellationToken.None);

        result.ShouldContain("Use ISO codes like GBP, BRL, EUR");
    }

    [Fact]
    public async Task GetExchangeRate_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetLatestRateAsync("GBP", "BRL", Arg.Any<CancellationToken>())
            .Returns<ExchangeRate?>(_ => throw new CurrencyApiException(
                "The exchange rate service is currently unavailable. Try again later."));

        var plugin = Plugin(client);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetExchangeRateAsync("GBP", "BRL", CancellationToken.None));

        result.ShouldBe("The exchange rate service is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task ConvertCurrency_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetLatestRateAsync("GBP", "BRL", Arg.Any<CancellationToken>())
            .Returns<ExchangeRate?>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(client);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.ConvertCurrencyAsync("250", "GBP", "BRL", CancellationToken.None));

        result.ShouldBe("The exchange rate request timed out. Try again later.");
    }

    [Fact]
    public async Task GetRateTrend_HttpRequestException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetTrendAsync("GBP", "BRL", Arg.Any<CancellationToken>())
            .Returns<RateTrend?>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetRateTrendAsync("GBP", "BRL", CancellationToken.None));

        result.ShouldBe("Could not reach the exchange rate service. Try again later.");
    }

    [Fact]
    public void Ctor_Throws_When_Client_Null()
    {
        Should.Throw<ArgumentNullException>(() => new CurrencyPlugin(null!));
    }
}
