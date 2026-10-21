using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Ledger;

namespace Scout.Plugin.Ledger.Tests;

public sealed class LedgerServiceCollectionExtensionsTests
{
    private static IConfiguration Config(bool? enabled)
    {
        var data = new Dictionary<string, string?>();
        if (enabled is not null)
        {
            data["Scout:Plugins:Ledger:Enabled"] = enabled.Value ? "true" : "false";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Disabled_Self_Gates_No_Plugin_No_Store()
    {
        var services = new ServiceCollection();
        services.AddLedgerPlugin(Config(enabled: false));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IScoutLocalStore>().ShouldBeNull();
    }

    [Fact]
    public void Enabled_By_Default_Registers_Plugin_And_Store()
    {
        var services = new ServiceCollection();
        services.AddLedgerPlugin(Config(enabled: null));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IScoutLocalStore>().ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_Registers_Ledger_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddLedgerPlugin(Config(enabled: true));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Ledger");
        plugin.TryGetFunction("record_expense", out _).ShouldBeTrue();
        plugin.TryGetFunction("record_income", out _).ShouldBeTrue();
        plugin.TryGetFunction("show_month_summary", out _).ShouldBeTrue();
        plugin.TryGetFunction("list_recent_entries", out _).ShouldBeTrue();
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddLedgerPlugin(Config(enabled: true)));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddLedgerPlugin(null!));
    }
}
