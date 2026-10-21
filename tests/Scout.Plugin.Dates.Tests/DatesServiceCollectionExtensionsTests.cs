using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Dates;

namespace Scout.Plugin.Dates.Tests;

public sealed class DatesServiceCollectionExtensionsTests
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
        services.AddDatesPlugin(Config(("Scout:Plugins:Dates:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
    }

    [Fact]
    public void Default_Config_Registers_Plugin()
    {
        var services = new ServiceCollection();
        services.AddDatesPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Enabled_True_Registers_Dates_Named_Plugin_With_All_Functions()
    {
        var services = new ServiceCollection();
        services.AddDatesPlugin(Config(("Scout:Plugins:Dates:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Dates");
        plugin.TryGetFunction("today", out _).ShouldBeTrue();
        plugin.TryGetFunction("days_until", out _).ShouldBeTrue();
        plugin.TryGetFunction("day_of_week", out _).ShouldBeTrue();
        plugin.TryGetFunction("date_difference", out _).ShouldBeTrue();
        plugin.TryGetFunction("add_to_date", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Registers_Shared_TimeProvider()
    {
        var services = new ServiceCollection();
        services.AddDatesPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetService<TimeProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void Binds_DefaultTimeZone_From_Configuration()
    {
        var services = new ServiceCollection();
        services.AddDatesPlugin(Config(("Scout:Plugins:Dates:DefaultTimeZone", "America/New_York")));
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<DatesOptions>>()
            .Value.DefaultTimeZone.ShouldBe("America/New_York");
    }

    [Fact]
    public void DefaultTimeZone_Defaults_To_Empty_Meaning_Machine_Local()
    {
        var services = new ServiceCollection();
        services.AddDatesPlugin(Config());
        var provider = services.BuildServiceProvider();

        // Empty default: the registration resolves it to the machine's local
        // zone at construction time.
        provider.GetRequiredService<IOptions<DatesOptions>>()
            .Value.DefaultTimeZone.ShouldBeEmpty();
    }

    [Fact]
    public void Disabled_Still_Binds_Options()
    {
        var services = new ServiceCollection();
        services.AddDatesPlugin(Config(("Scout:Plugins:Dates:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<DatesOptions>>().Value.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddDatesPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddDatesPlugin(null!));
    }
}
