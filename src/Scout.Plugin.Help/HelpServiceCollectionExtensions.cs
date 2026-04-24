using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Chat;
using Scout.Core.Plugins;

namespace Scout.Plugin.Help;

/// <summary>
/// DI composition for the Help plugin. Help ships with no configuration
/// surface, so there is a single overload. The plugin is intended to be
/// registered unconditionally by the host: it is the baseline heartbeat
/// that guarantees Scout answers <c>ping</c> / <c>help</c> / <c>version</c>
/// even before any feature plugin is wired up.
/// </summary>
public static class HelpServiceCollectionExtensions
{
    public static IServiceCollection AddHelpPlugin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IScoutPlugin, HelpPlugin>();
        services.AddSingleton<IMessageRouter, HelpMessageRouter>();

        return services;
    }
}
