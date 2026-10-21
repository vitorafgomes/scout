using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.WhatToWatch;
using Scout.Plugin.WhatToWatch.Abstractions;

namespace Scout.Plugin.WhatToWatch.Tests;

public sealed class WhatToWatchServiceCollectionExtensionsTests
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
        services.AddWhatToWatchPlugin(Config(("Scout:Plugins:WhatToWatch:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<ITvMazeClient>().ShouldBeNull();
        provider.GetService<ITmdbClient>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_By_Default_Registers_TvMaze_And_Plugin_But_No_Tmdb()
    {
        var services = new ServiceCollection();
        services.AddWhatToWatchPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<ITvMazeClient>().ShouldNotBeNull();
        provider.GetService<ITmdbClient>().ShouldBeNull();
    }

    [Fact]
    public void Blank_TmdbKey_Registers_No_Tmdb_Client()
    {
        var services = new ServiceCollection();
        services.AddWhatToWatchPlugin(Config(("Scout:Plugins:WhatToWatch:TmdbApiKey", "")));
        var provider = services.BuildServiceProvider();

        provider.GetService<ITvMazeClient>().ShouldNotBeNull();
        provider.GetService<ITmdbClient>().ShouldBeNull();
    }

    [Fact]
    public void With_TmdbKey_Registers_Tmdb_Client()
    {
        var services = new ServiceCollection();
        services.AddWhatToWatchPlugin(Config(("Scout:Plugins:WhatToWatch:TmdbApiKey", "tmdb_test")));
        var provider = services.BuildServiceProvider();

        provider.GetService<ITmdbClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_Registers_WhatToWatch_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddWhatToWatchPlugin(Config(("Scout:Plugins:WhatToWatch:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("WhatToWatch");
        plugin.TryGetFunction("search_show", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_next_episode", out _).ShouldBeTrue();
        plugin.TryGetFunction("recommend_shows", out _).ShouldBeTrue();
        plugin.TryGetFunction("where_to_watch", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_TvMaze_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddWhatToWatchPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(ITvMazeClient));

        http.BaseAddress.ShouldBe(new Uri("https://api.tvmaze.com/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/json");
    }

    [Fact]
    public void With_TmdbKey_Configures_Tmdb_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddWhatToWatchPlugin(Config(("Scout:Plugins:WhatToWatch:TmdbApiKey", "tmdb_test")));
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(ITmdbClient));

        http.BaseAddress.ShouldBe(new Uri("https://api.themoviedb.org/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddWhatToWatchPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddWhatToWatchPlugin(null!));
    }
}
