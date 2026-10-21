using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Recipes;
using Scout.Plugin.Recipes.Abstractions;

namespace Scout.Plugin.Recipes.Tests;

public sealed class RecipesServiceCollectionExtensionsTests
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
        services.AddRecipesPlugin(Config(("Scout:Plugins:Recipes:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IRecipesClient>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_By_Default_Registers_Client_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddRecipesPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IRecipesClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_Registers_Recipes_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddRecipesPlugin(Config(("Scout:Plugins:Recipes:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Recipes");
        plugin.TryGetFunction("find_recipes_by_ingredient", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_recipe", out _).ShouldBeTrue();
        plugin.TryGetFunction("suggest_random_recipe", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddRecipesPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IRecipesClient));

        http.BaseAddress.ShouldBe(new Uri("https://www.themealdb.com/api/json/v1/1/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/json");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddRecipesPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddRecipesPlugin(null!));
    }
}
