using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Chat;
using Scout.Core.Plugins;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus.Tests;

public class CloudStatusServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCloudStatusPlugin_Registers_Plugin_Functions_And_Router()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICloudHealthProvider>(_ =>
            throw new InvalidOperationException("provider not needed for this test"));

        services.AddCloudStatusPlugin();

        services.Should().Contain(d => d.ServiceType == typeof(CloudStatusFunctions));
        services.Should().Contain(d => d.ServiceType == typeof(IScoutPlugin));
        services.Should().Contain(d => d.ServiceType == typeof(IMessageRouter));
    }

    [Fact]
    public void AddCloudStatusPlugin_Null_Services_Throws()
    {
        Action act = () =>
            CloudStatusServiceCollectionExtensions.AddCloudStatusPlugin(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
