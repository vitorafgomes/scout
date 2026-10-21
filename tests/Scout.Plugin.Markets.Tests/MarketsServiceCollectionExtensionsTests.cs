using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Markets;
using Scout.Plugin.Markets.Abstractions;

namespace Scout.Plugin.Markets.Tests;

public sealed class MarketsServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var data = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Disabled_Self_Gates_No_Plugin_No_Clients()
    {
        var services = new ServiceCollection();
        services.AddMarketsPlugin(Config(("Scout:Plugins:Markets:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IStockQuoteClient>().ShouldBeNull();
        provider.GetService<ICryptoMarketClient>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_By_Default_Registers_Clients_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddMarketsPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IStockQuoteClient>().ShouldNotBeNull();
        provider.GetService<ICryptoMarketClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_Registers_Markets_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddMarketsPlugin(Config(("Scout:Plugins:Markets:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Markets");
        plugin.TryGetFunction("get_stock_quote", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_crypto_price", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_trending_crypto", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_Stooq_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddMarketsPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IStockQuoteClient));

        http.BaseAddress.ShouldBe(new Uri("https://stooq.com/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
    }

    [Fact]
    public void Enabled_Configures_CoinGecko_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddMarketsPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(ICryptoMarketClient));

        http.BaseAddress.ShouldBe(new Uri("https://api.coingecko.com/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        // No key configured: the optional demo header must be absent.
        http.DefaultRequestHeaders.Contains("x-cg-demo-api-key").ShouldBeFalse();
    }

    [Fact]
    public void CoinGecko_ApiKey_Adds_Demo_Header()
    {
        var services = new ServiceCollection();
        services.AddMarketsPlugin(Config(("Scout:Plugins:Markets:CoinGeckoApiKey", "demo-key-123")));
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(ICryptoMarketClient));

        http.DefaultRequestHeaders.Contains("x-cg-demo-api-key").ShouldBeTrue();
        http.DefaultRequestHeaders.GetValues("x-cg-demo-api-key").ShouldContain("demo-key-123");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddMarketsPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddMarketsPlugin(null!));
    }
}
