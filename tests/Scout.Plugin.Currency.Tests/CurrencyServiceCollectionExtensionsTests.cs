using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Currency;
using Scout.Plugin.Currency.Abstractions;

namespace Scout.Plugin.Currency.Tests;

public sealed class CurrencyServiceCollectionExtensionsTests
{
    private static IConfiguration Config(bool? enabled)
    {
        var data = new Dictionary<string, string?>();
        if (enabled is not null)
        {
            data["Scout:Plugins:Currency:Enabled"] = enabled.Value ? "true" : "false";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Disabled_Self_Gates_No_Plugin_No_Client()
    {
        var services = new ServiceCollection();
        services.AddCurrencyPlugin(Config(enabled: false));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<ICurrencyClient>().ShouldBeNull();
    }

    [Fact]
    public void Missing_Enabled_Defaults_To_On_And_Registers()
    {
        var services = new ServiceCollection();
        services.AddCurrencyPlugin(Config(enabled: null));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<ICurrencyClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_Registers_Currency_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddCurrencyPlugin(Config(enabled: true));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Currency");
        plugin.TryGetFunction("get_exchange_rate", out _).ShouldBeTrue();
        plugin.TryGetFunction("convert_currency", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_rate_trend", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_HttpClient_BaseAddress_Timeout_And_UserAgent()
    {
        var services = new ServiceCollection();
        services.AddCurrencyPlugin(Config(enabled: true));
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(ICurrencyClient));

        http.BaseAddress.ShouldBe(new Uri("https://api.frankfurter.dev/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddCurrencyPlugin(Config(enabled: true)));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddCurrencyPlugin(null!));
    }
}
