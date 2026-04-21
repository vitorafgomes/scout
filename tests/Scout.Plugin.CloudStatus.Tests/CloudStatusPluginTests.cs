using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Plugins;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus.Tests;

public class CloudStatusPluginTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Name_Is_CloudStatus()
    {
        new CloudStatusPlugin().Name.ShouldBe("CloudStatus");
    }

    [Fact]
    public void Capabilities_Are_ReadOnly()
    {
        new CloudStatusPlugin().Capabilities.IsReadOnly.ShouldBeTrue();
    }

    [Fact]
    public void Build_Registers_Every_KernelFunction()
    {
        var azure = new InMemoryCloudHealthProvider(new StatusReport(
            "Azure", "sub-1", HealthLevel.Healthy,
            Array.Empty<ServiceStatus>(), FixedTime));

        var services = new ServiceCollection();
        services.AddSingleton<ICloudHealthProvider>(azure);
        services.AddSingleton<CloudStatusFunctions>();
        using var provider = services.BuildServiceProvider();

        var kernelPlugin = new CloudStatusPlugin().Build(provider);

        kernelPlugin.Name.ShouldBe("CloudStatus");
        kernelPlugin.Select(f => f.Name)
                    .ShouldBe(["get_all_cloud_health", "get_cloud_health"]);
    }

    [Fact]
    public void Build_Throws_When_Services_Null()
    {
        Action build = () => new CloudStatusPlugin().Build(null!);

        build.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Can_Register_In_PluginRegistry_Because_ReadOnly()
    {
        var registry = new PluginRegistry();

        Action register = () => registry.Register(new CloudStatusPlugin());

        register.ShouldNotThrow();
        registry.Plugins.ShouldHaveSingleItem()
                .Name.ShouldBe("CloudStatus");
    }
}
