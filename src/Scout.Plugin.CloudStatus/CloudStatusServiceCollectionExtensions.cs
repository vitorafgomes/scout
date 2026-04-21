using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Chat;
using Scout.Core.Plugins;

namespace Scout.Plugin.CloudStatus;

/// <summary>
/// DI composition for the CloudStatus plugin. This PR wires the plugin,
/// functions, and keyword router; the concrete cloud providers
/// (Azure / AWS / Oracle Cloud) land in follow-up PRs. Consumers that call
/// <see cref="AddCloudStatusPlugin"/> today should also register at least
/// one <c>ICloudHealthProvider</c> implementation — otherwise the plugin
/// reports "No cloud providers are configured."
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
}
