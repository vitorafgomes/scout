using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Nutrition;
using Scout.Plugin.Nutrition.Abstractions;

namespace Scout.Plugin.Nutrition.Tests;

public sealed class NutritionServiceCollectionExtensionsTests
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
        services.AddNutritionPlugin(Config(("Scout:Plugins:Nutrition:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<INutritionClient>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_By_Default_Registers_Client_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddNutritionPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<INutritionClient>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_Registers_Nutrition_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddNutritionPlugin(Config(("Scout:Plugins:Nutrition:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Nutrition");
        plugin.TryGetFunction("find_food_product", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_nutrition_facts", out _).ShouldBeTrue();
        plugin.TryGetFunction("get_nutrition_by_barcode", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Configures_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddNutritionPlugin(Config());
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(INutritionClient));

        http.BaseAddress.ShouldBe(new Uri("https://world.openfoodfacts.org/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/json");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddNutritionPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddNutritionPlugin(null!));
    }
}
