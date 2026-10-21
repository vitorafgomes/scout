using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Lists;

namespace Scout.Plugin.Lists.Tests;

public sealed class ListsServiceCollectionExtensionsTests
{
    private static IConfiguration Config(bool? enabled)
    {
        var data = new Dictionary<string, string?>();
        if (enabled is not null)
        {
            data["Scout:Plugins:Lists:Enabled"] = enabled.Value ? "true" : "false";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Enabled_False_Self_Gates_No_Plugin()
    {
        var services = new ServiceCollection();
        services.AddListsPlugin(Config(enabled: false));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IScoutLocalStore>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_Default_When_Missing_Registers_Plugin_And_Store()
    {
        var services = new ServiceCollection();
        services.AddListsPlugin(Config(enabled: null));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IScoutLocalStore>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_True_Registers_Lists_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddListsPlugin(Config(enabled: true));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Lists");
        plugin.TryGetFunction("add_to_list", out _).ShouldBeTrue();
        plugin.TryGetFunction("show_list", out _).ShouldBeTrue();
        plugin.TryGetFunction("remove_from_list", out _).ShouldBeTrue();
        plugin.TryGetFunction("clear_list", out _).ShouldBeTrue();
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddListsPlugin(Config(enabled: true)));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddListsPlugin(null!));
    }
}
