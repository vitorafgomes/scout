using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Weather;
using Scout.Plugin.Weather.Abstractions;

namespace Scout.Plugin.Weather.Tests;

public sealed class WeatherServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries)
    {
        var data = new Dictionary<string, string?>();
        foreach (var (key, value) in entries)
        {
            data[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Enabled_False_Self_Gates_No_Plugin_No_Client()
    {
        var services = new ServiceCollection();
        services.AddWeatherPlugin(Config(("Scout:Plugins:Weather:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IWeatherClient>().ShouldBeNull();
    }

    [Fact]
    public void Default_Enabled_Registers_Client_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddWeatherPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IWeatherClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Explicit_Enabled_True_Registers_Weather_Named_Plugin()
    {
        var services = new ServiceCollection();
        services.AddWeatherPlugin(Config(("Scout:Plugins:Weather:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Weather");
        plugin.TryGetFunction("get_current_weather", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_forecast", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_air_quality", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_HttpClient_Timeout_And_UserAgent()
    {
        var services = new ServiceCollection();
        services.AddWeatherPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IWeatherClient));

        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddWeatherPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddWeatherPlugin(null!));
    }
}
