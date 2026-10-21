using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Sports;
using Scout.Plugin.Sports.Abstractions;

namespace Scout.Plugin.Sports.Tests;

public sealed class SportsServiceCollectionExtensionsTests
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
        services.AddSportsPlugin(Config(("Scout:Plugins:Sports:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<ISportsClient>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_By_Default_Registers_Client_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddSportsPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<ISportsClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_Registers_Sports_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddSportsPlugin(Config(("Scout:Plugins:Sports:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Sports");
        plugin.TryGetFunction("get_last_result", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_next_fixture", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_league_table", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_HttpClient_With_Free_Test_Key()
    {
        var services = new ServiceCollection();
        services.AddSportsPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(ISportsClient));

        http.BaseAddress.ShouldBe(new Uri("https://www.thesportsdb.com/api/v1/json/3/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
    }

    [Fact]
    public void FootballDataToken_Upgrades_The_Base_Address_Key_Segment()
    {
        var services = new ServiceCollection();
        services.AddSportsPlugin(Config(("Scout:Plugins:Sports:FootballDataToken", "premium123")));
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(ISportsClient));

        http.BaseAddress.ShouldBe(new Uri("https://www.thesportsdb.com/api/v1/json/premium123/"));
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddSportsPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddSportsPlugin(null!));
    }
}
