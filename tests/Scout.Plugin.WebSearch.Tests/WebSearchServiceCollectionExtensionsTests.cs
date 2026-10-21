using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.WebSearch;
using Scout.Plugin.WebSearch.Abstractions;

namespace Scout.Plugin.WebSearch.Tests;

public sealed class WebSearchServiceCollectionExtensionsTests
{
    private const string SearxKey = "Scout:Plugins:WebSearch:SearxUrl";

    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var data = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Disabled_Self_Gates_No_Plugin_No_Client()
    {
        var services = new ServiceCollection();
        services.AddWebSearchPlugin(Config(("Scout:Plugins:WebSearch:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IWebSearchClient>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_By_Default_Registers_Client_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddWebSearchPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IWebSearchClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Blank_Searx_Gates_Down_To_DuckDuckGo_Only_Surface()
    {
        var services = new ServiceCollection();
        services.AddWebSearchPlugin(Config());
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("WebSearch");
        plugin.TryGetFunction("search_web", out _).ShouldBeFalse();
        plugin.TryGetFunction("get_instant_answer", out _).ShouldBeTrue();
        plugin.TryGetFunction("fetch_page", out _).ShouldBeTrue();
    }

    [Fact]
    public void Configured_Searx_Exposes_All_Three_Functions()
    {
        var services = new ServiceCollection();
        services.AddWebSearchPlugin(Config((SearxKey, "https://searx.local")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.TryGetFunction("search_web", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_instant_answer", out _).ShouldBeTrue();
        plugin.TryGetFunction("fetch_page", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddWebSearchPlugin(Config((SearxKey, "https://searx.local")));
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IWebSearchClient));

        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddWebSearchPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddWebSearchPlugin(null!));
    }
}
