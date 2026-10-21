using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.SelfInfo;

namespace Scout.Plugin.SelfInfo.Tests;

/// <summary>
/// Verifies the security-critical self-gate: the plugin defaults to disabled and
/// only registers a kernel plugin when an operator explicitly opts in.
/// </summary>
public sealed class SelfInfoServiceCollectionExtensionsTests
{
    private static IConfiguration Config(bool? enabled)
    {
        var data = new Dictionary<string, string?>();
        if (enabled is not null)
        {
            data["Scout:Plugins:SelfInfo:Enabled"] = enabled.Value.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Missing_Enabled_Defaults_To_Disabled_No_Plugin()
    {
        var services = new ServiceCollection();
        services.AddSelfInfoPlugin(Config(enabled: null));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
    }

    [Fact]
    public void Enabled_False_Self_Gates_No_Plugin()
    {
        var services = new ServiceCollection();
        services.AddSelfInfoPlugin(Config(enabled: false));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
    }

    [Fact]
    public void Enabled_True_Registers_Single_SelfInfo_Plugin()
    {
        var services = new ServiceCollection();
        services.AddSelfInfoPlugin(Config(enabled: true));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("SelfInfo");
    }

    [Fact]
    public void Enabled_True_Exposes_All_Five_Functions()
    {
        var services = new ServiceCollection();
        services.AddSelfInfoPlugin(Config(enabled: true));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.TryGetFunction("where_installed", out _).ShouldBeTrue();
        plugin.TryGetFunction("system_info", out _).ShouldBeTrue();
        plugin.TryGetFunction("resource_usage", out _).ShouldBeTrue();
        plugin.TryGetFunction("active_configuration", out _).ShouldBeTrue();
        plugin.TryGetFunction("worker_status", out _).ShouldBeTrue();
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddSelfInfoPlugin(Config(enabled: true)));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddSelfInfoPlugin(null!));
    }
}
