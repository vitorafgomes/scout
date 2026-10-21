using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.WorldClock;

namespace Scout.Plugin.WorldClock.Tests;

public sealed class WorldClockServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var data = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Enabled_False_Self_Gates_No_Plugin()
    {
        var services = new ServiceCollection();
        services.AddWorldClockPlugin(Config(("Scout:Plugins:WorldClock:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
    }

    [Fact]
    public void Absent_Section_Registers_Plugin_And_Host_Still_Starts()
    {
        var services = new ServiceCollection();
        services.AddWorldClockPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Enabled_True_Registers_WorldClock_Named_Plugin_With_All_Functions()
    {
        var services = new ServiceCollection();
        services.AddWorldClockPlugin(Config(("Scout:Plugins:WorldClock:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("WorldClock");
        plugin.TryGetFunction("current_time_in_city", out _).ShouldBeTrue();
        plugin.TryGetFunction("convert_time_between_cities", out _).ShouldBeTrue();
        plugin.TryGetFunction("time_difference_between_cities", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Registers_Shared_TimeProvider()
    {
        var services = new ServiceCollection();
        services.AddWorldClockPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetService<TimeProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void Binds_DefaultTimeZone_From_Configuration()
    {
        var services = new ServiceCollection();
        services.AddWorldClockPlugin(
            Config(("Scout:Plugins:WorldClock:DefaultTimeZone", "America/New_York")));
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<WorldClockOptions>>()
            .Value.DefaultTimeZone.ShouldBe("America/New_York");
    }

    [Fact]
    public void DefaultTimeZone_Defaults_To_Empty_Meaning_Machine_Local()
    {
        var services = new ServiceCollection();
        services.AddWorldClockPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<WorldClockOptions>>()
            .Value.DefaultTimeZone.ShouldBeEmpty();
    }

    [Fact]
    public void Disabled_Still_Binds_Options()
    {
        var services = new ServiceCollection();
        services.AddWorldClockPlugin(Config(("Scout:Plugins:WorldClock:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<WorldClockOptions>>().Value.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddWorldClockPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddWorldClockPlugin(null!));
    }
}
