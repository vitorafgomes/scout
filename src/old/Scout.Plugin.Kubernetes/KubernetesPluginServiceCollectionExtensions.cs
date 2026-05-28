using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Core.Plugins;
using Scout.Core.Features;
using Scout.Plugin.Kubernetes.Internal;

namespace Scout.Plugin.Kubernetes;

/// <summary>DI registration helpers for the Kubernetes plugin.</summary>
public static class KubernetesPluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers options, the client factory, the credential validator,
    /// the read-ops seam, the kernel functions and the plugin itself,
    /// then enrolls the plugin with the Features subsystem.
    /// </summary>
    public static IServiceCollection AddKubernetesFeaturePlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<KubernetesPluginOptions>()
            .Bind(configuration.GetSection(KubernetesPluginOptions.SectionName));

        services.TryAddSingleton<IKubernetesClientFactory, KubernetesClientFactory>();
        services.AddSingleton<IFeatureCredentialValidator, KubeconfigValidator>();
        services.TryAddSingleton<IKubernetesReadOps, KubernetesReadOps>();
        services.TryAddSingleton<KubernetesFunctions>(sp => new KubernetesFunctions(
            sp.GetRequiredService<IKubernetesReadOps>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KubernetesFunctions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<KubernetesPluginOptions>>()));

        services.AddSingleton<KubernetesPlugin>();
        services.AddSingleton<IScoutPlugin>(sp => sp.GetRequiredService<KubernetesPlugin>());
        services.AddFeaturePlugin<KubernetesPlugin>(KubernetesPlugin.Id);

        return services;
    }
}
