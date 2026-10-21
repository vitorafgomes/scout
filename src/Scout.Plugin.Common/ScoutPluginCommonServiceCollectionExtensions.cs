using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Plugin.Common.Abstractions;

namespace Scout.Plugin.Common;

/// <summary>
/// Shared registrations for the day-to-day plugins: the Scout-local JSON store
/// and the injected <see cref="TimeProvider"/>. Idempotent, so any plugin that
/// needs the store can call it without ordering concerns.
/// </summary>
public static class ScoutPluginCommonServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="ScoutLocalStoreOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:LocalStore";

    /// <summary>
    /// Register <see cref="IScoutLocalStore"/> and a <see cref="TimeProvider"/>.
    /// Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddScoutPluginCommon(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ScoutLocalStoreOptions>(configuration.GetSection(ConfigSection));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IScoutLocalStore, JsonFileScoutLocalStore>();
        return services;
    }
}
