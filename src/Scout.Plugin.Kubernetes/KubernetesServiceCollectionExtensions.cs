using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace Scout.Plugin.Kubernetes;

/// <summary>
/// DI composition for the read-only Kubernetes plugin. Binds <see cref="KubernetesOptions"/>
/// from "Scout:Plugins:Kubernetes" and registers a <see cref="KernelPlugin"/> named
/// "Kubernetes" only when a kubeconfig path and at least one allowed context are configured.
/// </summary>
public static class KubernetesServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="KubernetesOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Kubernetes";

    /// <summary>
    /// Register the Kubernetes plugin. No-op when the kubeconfig path is blank or no context
    /// is allowed. Clients are built on the first call, so registration does no I/O.
    /// </summary>
    public static IServiceCollection AddKubernetesPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(ConfigSection).Get<KubernetesOptions>();
        if (options is null
            || string.IsNullOrWhiteSpace(options.KubeconfigPath)
            || !options.Contexts.Any(c => !string.IsNullOrWhiteSpace(c.Name)))
        {
            return services;
        }

        services.AddSingleton(_ => new KubernetesPlugin(options));
        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            sp.GetRequiredService<KubernetesPlugin>(), "Kubernetes"));

        return services;
    }
}
