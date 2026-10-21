using Microsoft.SemanticKernel;
using Scout.Plugin.Markets;
using Scout.Plugin.Markets.Abstractions;

namespace Scout.Plugin.Markets.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies on
/// for function-calling: stable snake_case function names plus non-empty
/// descriptions on the functions and every parameter.
/// </summary>
public sealed class MarketsPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var stocks = Substitute.For<IStockQuoteClient>();
        var crypto = Substitute.For<ICryptoMarketClient>();
        return KernelPluginFactory.CreateFromObject(new MarketsPlugin(stocks, crypto), "Markets");
    }

    [Theory]
    [InlineData("get_stock_quote")]
    [InlineData("get_crypto_price")]
    [InlineData("get_trending_crypto")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Fact]
    public void Exposes_Exactly_The_Expected_Functions()
    {
        var plugin = BuildPlugin();

        plugin.Select(f => f.Name).OrderBy(n => n).ShouldBe(
            new[] { "get_crypto_price", "get_stock_quote", "get_trending_crypto" });
    }

    [Theory]
    [InlineData("get_stock_quote")]
    [InlineData("get_crypto_price")]
    [InlineData("get_trending_crypto")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("get_stock_quote")]
    [InlineData("get_crypto_price")]
    [InlineData("get_trending_crypto")]
    public void Every_Parameter_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        foreach (var parameter in function!.Metadata.Parameters)
        {
            parameter.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetStockQuote_Exposes_Symbol_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("get_stock_quote", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Select(p => p.Name).ShouldContain("symbol");
    }

    [Fact]
    public void GetCryptoPrice_Exposes_Coin_And_Currency_Parameters()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("get_crypto_price", out var function).ShouldBeTrue();

        var names = function!.Metadata.Parameters.Select(p => p.Name).ToList();
        names.ShouldContain("coin");
        names.ShouldContain("currency");
    }

    [Fact]
    public void GetTrendingCrypto_Has_No_Model_Visible_Parameters()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("get_trending_crypto", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.ShouldBeEmpty();
    }
}
