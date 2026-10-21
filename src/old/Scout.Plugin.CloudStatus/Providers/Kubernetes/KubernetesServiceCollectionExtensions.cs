using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus.Providers.Kubernetes;

/// <summary>
/// DI composition for the Kubernetes cloud health provider. Registers the
/// configuration block under "Scout:Plugins:CloudStatus:Kubernetes", wires
/// <see cref="K8sHealthSource"/> as the default <see cref="IK8sHealthSource"/>,
/// and registers <see cref="K8sCloudHealthProvider"/> as an
/// <see cref="ICloudHealthProvider"/> so it joins the aggregate fan-out.
/// </summary>
public static class KubernetesServiceCollectionExtensions
{
    public const string ConfigSection = "Scout:Plugins:CloudStatus:Kubernetes";

    public static IServiceCollection AddKubernetesCloudHealthProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<K8sCloudHealthOptions>(configuration.GetSection(ConfigSection));
        services.AddSingleton<IK8sHealthSource, K8sHealthSource>();
        services.AddSingleton<ICloudHealthProvider, K8sCloudHealthProvider>();

        return services;
    }
}
