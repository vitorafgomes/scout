using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Holidays;
using Scout.Plugin.Holidays.Abstractions;

namespace Scout.Plugin.Holidays.Tests;

public sealed class HolidaysServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var data = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Disabled_Self_Gates_No_Plugin_No_Client()
    {
        var services = new ServiceCollection();
        services.AddHolidaysPlugin(Config(("Scout:Plugins:Holidays:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IHolidaysClient>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_By_Default_Registers_Client_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddHolidaysPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IHolidaysClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_Registers_Holidays_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddHolidaysPlugin(Config(("Scout:Plugins:Holidays:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Holidays");
        plugin.TryGetFunction("get_next_holidays", out _).ShouldBeTrue();
        plugin.TryGetFunction("is_holiday", out _).ShouldBeTrue();
        plugin.TryGetFunction("list_holidays", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_long_weekends", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddHolidaysPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IHolidaysClient));

        http.BaseAddress.ShouldBe(new Uri("https://date.nager.at/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/json");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddHolidaysPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddHolidaysPlugin(null!));
    }
}
