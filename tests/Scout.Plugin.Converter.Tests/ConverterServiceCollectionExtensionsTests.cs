using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Converter;

namespace Scout.Plugin.Converter.Tests;

public sealed class ConverterServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var data = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Disabled_Self_Gates_No_Plugin()
    {
        var services = new ServiceCollection();
        services.AddConverterPlugin(Config(("Scout:Plugins:Converter:Enabled", "false")));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
    }

    [Fact]
    public void Enabled_By_Default_Registers_Plugin()
    {
        var services = new ServiceCollection();
        services.AddConverterPlugin(Config());
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Enabled_Registers_Converter_Named_Plugin_With_Functions()
    {
        var services = new ServiceCollection();
        services.AddConverterPlugin(Config(("Scout:Plugins:Converter:Enabled", "true")));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Converter");
        plugin.TryGetFunction("convert_units", out _).ShouldBeTrue();
        plugin.TryGetFunction("calculate", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Binds_MaxExpressionLength_Option()
    {
        var services = new ServiceCollection();
        services.AddConverterPlugin(Config(("Scout:Plugins:Converter:MaxExpressionLength", "16")));
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<ConverterOptions>>().Value;
        options.MaxExpressionLength.ShouldBe(16);
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddConverterPlugin(Config()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddConverterPlugin(null!));
    }
}
