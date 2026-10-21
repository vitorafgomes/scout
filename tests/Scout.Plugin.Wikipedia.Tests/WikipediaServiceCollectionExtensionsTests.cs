using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Wikipedia;
using Scout.Plugin.Wikipedia.Abstractions;

namespace Scout.Plugin.Wikipedia.Tests;

public sealed class WikipediaServiceCollectionExtensionsTests
{
    private static IConfiguration Config(bool? enabled)
    {
        var data = new Dictionary<string, string?>();
        if (enabled is not null)
        {
            data["Scout:Plugins:Wikipedia:Enabled"] = enabled.Value ? "true" : "false";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Default_When_Section_Absent_Registers_Client_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddWikipediaPlugin(Config(enabled: null));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IWikipediaClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_True_Registers_Client_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddWikipediaPlugin(Config(enabled: true));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IWikipediaClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_False_Self_Gates_No_Plugin_No_Client()
    {
        var services = new ServiceCollection();
        services.AddWikipediaPlugin(Config(enabled: false));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IWikipediaClient>().ShouldBeNull();
    }

    [Fact]
    public void Registers_Wikipedia_Named_Plugin_With_Expected_Functions()
    {
        var services = new ServiceCollection();
        services.AddWikipediaPlugin(Config(enabled: true));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Wikipedia");
        plugin.TryGetFunction("search_wikipedia", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_article_summary", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_on_this_day", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_HttpClient_BaseAddress_Timeout_And_UserAgent()
    {
        var services = new ServiceCollection();
        services.AddWikipediaPlugin(Config(enabled: true));
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IWikipediaClient));

        http.BaseAddress.ShouldBe(new Uri("https://api.wikimedia.org/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/json");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddWikipediaPlugin(Config(enabled: true)));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddWikipediaPlugin(null!));
    }
}
