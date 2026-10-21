using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Parcels;
using Scout.Plugin.Parcels.Abstractions;

namespace Scout.Plugin.Parcels.Tests;

public sealed class ParcelsServiceCollectionExtensionsTests
{
    private static IConfiguration Config(string? apiKey)
    {
        var data = new Dictionary<string, string?>();
        if (apiKey is not null)
        {
            data["Scout:Plugins:Parcels:ApiKey"] = apiKey;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void No_ApiKey_Self_Gates_No_Plugin_No_Client()
    {
        var services = new ServiceCollection();
        services.AddParcelsPlugin(Config(apiKey: ""));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IParcelsClient>().ShouldBeNull();
    }

    [Fact]
    public void Missing_ApiKey_Section_Self_Gates()
    {
        var services = new ServiceCollection();
        services.AddParcelsPlugin(Config(apiKey: null));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IParcelsClient>().ShouldBeNull();
    }

    [Fact]
    public void With_ApiKey_Registers_Client_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddParcelsPlugin(Config(apiKey: "test-key"));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IParcelsClient>().ShouldNotBeNull();
    }

    [Fact]
    public void With_ApiKey_Registers_Parcels_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddParcelsPlugin(Config(apiKey: "test-key"));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Parcels");
        plugin.TryGetFunction("track_package", out _).ShouldBeTrue();
        plugin.TryGetFunction("watch_package", out _).ShouldBeTrue();
        plugin.TryGetFunction("list_watched_packages", out _).ShouldBeTrue();
        plugin.TryGetFunction("unwatch_package", out _).ShouldBeTrue();
    }

    [Fact]
    public void With_ApiKey_Configures_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddParcelsPlugin(Config(apiKey: "test-key"));
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IParcelsClient));

        http.BaseAddress.ShouldBe(new Uri("https://api.ship24.com/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.Authorization!.Scheme.ShouldBe("Bearer");
        http.DefaultRequestHeaders.Authorization!.Parameter.ShouldBe("test-key");
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/json");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddParcelsPlugin(Config("k")));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddParcelsPlugin(null!));
    }
}
