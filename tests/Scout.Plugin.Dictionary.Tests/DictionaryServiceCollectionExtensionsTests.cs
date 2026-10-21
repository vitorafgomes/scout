using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Dictionary;
using Scout.Plugin.Dictionary.Abstractions;

namespace Scout.Plugin.Dictionary.Tests;

public sealed class DictionaryServiceCollectionExtensionsTests
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
        services.AddDictionaryPlugin(Config(("Scout:Plugins:Dictionary:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IDefinitionsClient>().ShouldBeNull();
        provider.GetService<IDatamuseClient>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_By_Default_Registers_Clients_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddDictionaryPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IDefinitionsClient>().ShouldNotBeNull();
        provider.GetService<IDatamuseClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_Registers_Dictionary_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddDictionaryPlugin(Config(("Scout:Plugins:Dictionary:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Dictionary");
        plugin.TryGetFunction("define_word", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_synonyms", out _).ShouldBeTrue();
        plugin.TryGetFunction("check_spelling", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_Definitions_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddDictionaryPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IDefinitionsClient));

        http.BaseAddress.ShouldBe(new Uri("https://api.dictionaryapi.dev/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/json");
    }

    [Fact]
    public void Enabled_Configures_Datamuse_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddDictionaryPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IDatamuseClient));

        http.BaseAddress.ShouldBe(new Uri("https://api.datamuse.com/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/json");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddDictionaryPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddDictionaryPlugin(null!));
    }
}
