using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Chat;
using Scout.Core.Plugins;
using Scout.Plugin.CloudStatus.Providers.Aws;
using Scout.Plugin.CloudStatus.Providers.Azure;
using Scout.Plugin.CloudStatus.Providers.Kubernetes;
using Scout.Plugin.CloudStatus.Providers.Oci;

namespace Scout.Plugin.CloudStatus;

/// <summary>
/// DI composition for the CloudStatus plugin. Two overloads:
///
/// <list type="bullet">
/// <item>
///   <see cref="AddCloudStatusPlugin(IServiceCollection)"/> — core wiring
///   only. Consumers register their own <c>ICloudHealthProvider</c> values.
/// </item>
/// <item>
///   <see cref="AddCloudStatusPlugin(IServiceCollection, IConfiguration)"/> —
///   core wiring + conditional registration of each first-party provider
///   (Azure, AWS, OCI, Kubernetes) when its configuration block is populated.
///   Missing blocks silently skip that provider — an unconfigured cloud
///   should never break host startup.
/// </item>
/// </list>
/// </summary>
public static class CloudStatusServiceCollectionExtensions
{
    public static IServiceCollection AddCloudStatusPlugin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<CloudStatusFunctions>();
        services.AddSingleton<IScoutPlugin, CloudStatusPlugin>();
        services.AddSingleton<IMessageRouter, CloudStatusMessageRouter>();

        return services;
    }

    public static IServiceCollection AddCloudStatusPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddCloudStatusPlugin();

        if (IsAzureConfigured(configuration))
        {
            services.AddAzureCloudHealthProvider(configuration);
        }
        if (IsAwsConfigured(configuration))
        {
            services.AddAwsCloudHealthProvider(configuration);
        }
        if (IsOciEnabled(configuration))
        {
            services.AddOciCloudHealthProvider(configuration);
        }
        if (IsKubernetesConfigured(configuration))
        {
            services.AddKubernetesCloudHealthProvider(configuration);
        }

        return services;
    }

    private static bool IsAzureConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(
            configuration[$"{AzureServiceCollectionExtensions.ConfigSection}:SubscriptionId"]);

    private static bool IsAwsConfigured(IConfiguration configuration) =>
        configuration.GetSection(AwsServiceCollectionExtensions.ConfigSection).Exists();

    private static bool IsOciEnabled(IConfiguration configuration)
    {
        // OCI endpoint is public — enabling is a boolean decision.
        // Default: enabled when the config section exists, explicit true
        // keeps it enabled when a user adds other blocks but not this one.
        var section = configuration.GetSection(OciServiceCollectionExtensions.ConfigSection);
        if (!section.Exists())
        {
            return false;
        }
        var enabled = section["Enabled"];
        return string.IsNullOrWhiteSpace(enabled) || bool.TryParse(enabled, out var e) && e;
    }

    private static bool IsKubernetesConfigured(IConfiguration configuration)
    {
        var section = configuration.GetSection(KubernetesServiceCollectionExtensions.ConfigSection);
        if (!section.Exists())
        {
            return false;
        }
        var inCluster = bool.TryParse(section["UseInClusterConfig"], out var v) && v;
        var kubeconfig = section["KubeconfigPath"];
        return inCluster || !string.IsNullOrWhiteSpace(kubeconfig);
    }
}
