using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.News;
using Scout.Plugin.News.Abstractions;

namespace Scout.Plugin.News.Tests;

public sealed class NewsServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var data = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Disabled_Self_Gates_No_Plugin_No_Clients_No_Store()
    {
        var services = new ServiceCollection();
        services.AddNewsPlugin(Config(("Scout:Plugins:News:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IFeedClient>().ShouldBeNull();
        provider.GetService<IHackerNewsClient>().ShouldBeNull();
        provider.GetService<IScoutLocalStore>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_By_Default_Registers_Clients_Store_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddNewsPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IFeedClient>().ShouldNotBeNull();
        provider.GetService<IHackerNewsClient>().ShouldNotBeNull();
        provider.GetService<IScoutLocalStore>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_Registers_News_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddNewsPlugin(Config(("Scout:Plugins:News:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("News");
        plugin.TryGetFunction("get_top_headlines", out _).ShouldBeTrue();
        plugin.TryGetFunction("search_news", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_hacker_news_top", out _).ShouldBeTrue();
        plugin.TryGetFunction("save_to_reading_list", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_reading_list", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_HackerNews_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddNewsPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IHackerNewsClient));

        http.BaseAddress.ShouldBe(new Uri("https://hn.algolia.com/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/json");
    }

    [Fact]
    public void Enabled_Configures_Feed_HttpClient_With_UserAgent()
    {
        var services = new ServiceCollection();
        services.AddNewsPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IFeedClient));

        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddNewsPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddNewsPlugin(null!));
    }
}
