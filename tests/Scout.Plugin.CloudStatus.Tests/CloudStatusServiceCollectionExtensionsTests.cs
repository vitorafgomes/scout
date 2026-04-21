using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Chat;
using Scout.Core.Plugins;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus.Tests;

public class CloudStatusServiceCollectionExtensionsTests
{
    [Fact]
    public void Basic_Overload_Registers_Plugin_Functions_And_Router()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICloudHealthProvider>(_ =>
            throw new InvalidOperationException("provider not needed for this test"));

        services.AddCloudStatusPlugin();

        services.ShouldContain(d => d.ServiceType == typeof(CloudStatusFunctions));
        services.ShouldContain(d => d.ServiceType == typeof(IScoutPlugin));
        services.ShouldContain(d => d.ServiceType == typeof(IMessageRouter));
    }

    [Fact]
    public void Basic_Overload_Null_Services_Throws()
    {
        Action act = () =>
            CloudStatusServiceCollectionExtensions.AddCloudStatusPlugin(null!);

        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Config_Overload_Registers_Plugin_Core()
    {
        var services = new ServiceCollection();

        services.AddCloudStatusPlugin(BuildConfig(new Dictionary<string, string?>()));

        services.ShouldContain(d => d.ServiceType == typeof(CloudStatusFunctions));
        services.ShouldContain(d => d.ServiceType == typeof(IScoutPlugin));
        services.ShouldContain(d => d.ServiceType == typeof(IMessageRouter));
    }

    [Fact]
    public void Config_Overload_Skips_Every_Provider_By_Default()
    {
        var services = new ServiceCollection();

        services.AddCloudStatusPlugin(BuildConfig(new Dictionary<string, string?>()));

        services.ShouldNotContain(d => d.ServiceType == typeof(ICloudHealthProvider));
    }

    [Fact]
    public void Config_Overload_Registers_Azure_When_SubscriptionId_Is_Set()
    {
        var services = new ServiceCollection();

        services.AddCloudStatusPlugin(BuildConfig(new Dictionary<string, string?>
        {
            ["Scout:Plugins:CloudStatus:Azure:SubscriptionId"] = "00000000-0000-0000-0000-000000000001",
        }));

        ProviderCount(services).ShouldBe(1);
    }

    [Fact]
    public void Config_Overload_Registers_Aws_When_Section_Exists()
    {
        var services = new ServiceCollection();

        services.AddCloudStatusPlugin(BuildConfig(new Dictionary<string, string?>
        {
            ["Scout:Plugins:CloudStatus:Aws:Region"] = "eu-west-1",
        }));

        ProviderCount(services).ShouldBe(1);
    }

    [Fact]
    public void Config_Overload_Registers_Oci_When_Enabled()
    {
        var services = new ServiceCollection();

        services.AddCloudStatusPlugin(BuildConfig(new Dictionary<string, string?>
        {
            ["Scout:Plugins:CloudStatus:OracleCloud:Enabled"] = "true",
        }));

        ProviderCount(services).ShouldBe(1);
    }

    [Fact]
    public void Config_Overload_Skips_Oci_When_Enabled_False()
    {
        var services = new ServiceCollection();

        services.AddCloudStatusPlugin(BuildConfig(new Dictionary<string, string?>
        {
            ["Scout:Plugins:CloudStatus:OracleCloud:Enabled"] = "false",
        }));

        ProviderCount(services).ShouldBe(0);
    }

    [Fact]
    public void Config_Overload_Registers_Kubernetes_When_InCluster_True()
    {
        var services = new ServiceCollection();

        services.AddCloudStatusPlugin(BuildConfig(new Dictionary<string, string?>
        {
            ["Scout:Plugins:CloudStatus:Kubernetes:UseInClusterConfig"] = "true",
        }));

        ProviderCount(services).ShouldBe(1);
    }

    [Fact]
    public void Config_Overload_Registers_Kubernetes_When_Kubeconfig_Set()
    {
        var services = new ServiceCollection();

        services.AddCloudStatusPlugin(BuildConfig(new Dictionary<string, string?>
        {
            ["Scout:Plugins:CloudStatus:Kubernetes:KubeconfigPath"] = "/tmp/kubeconfig",
        }));

        ProviderCount(services).ShouldBe(1);
    }

    [Fact]
    public void Config_Overload_Skips_Kubernetes_When_Section_Empty_Of_Sources()
    {
        var services = new ServiceCollection();

        services.AddCloudStatusPlugin(BuildConfig(new Dictionary<string, string?>
        {
            ["Scout:Plugins:CloudStatus:Kubernetes:ClusterName"] = "anything",
        }));

        ProviderCount(services).ShouldBe(0);
    }

    [Fact]
    public void Config_Overload_Registers_All_Four_Together()
    {
        var services = new ServiceCollection();

        services.AddCloudStatusPlugin(BuildConfig(new Dictionary<string, string?>
        {
            ["Scout:Plugins:CloudStatus:Azure:SubscriptionId"] = "00000000-0000-0000-0000-000000000001",
            ["Scout:Plugins:CloudStatus:Aws:Region"] = "eu-west-1",
            ["Scout:Plugins:CloudStatus:OracleCloud:Enabled"] = "true",
            ["Scout:Plugins:CloudStatus:Kubernetes:UseInClusterConfig"] = "true",
        }));

        ProviderCount(services).ShouldBe(4);
    }

    [Fact]
    public void Config_Overload_Null_Config_Throws()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddCloudStatusPlugin((IConfiguration)null!);

        act.ShouldThrow<ArgumentNullException>();
    }

    private static int ProviderCount(IServiceCollection services) =>
        services.Count(d => d.ServiceType == typeof(ICloudHealthProvider));

    private static IConfiguration BuildConfig(IDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
