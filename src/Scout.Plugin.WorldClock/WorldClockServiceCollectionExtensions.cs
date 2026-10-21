using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.WorldClock.Abstractions;

namespace Scout.Plugin.WorldClock;

/// <summary>
/// DI composition for the read-only WorldClock plugin. Binds
/// <see cref="WorldClockOptions"/> from "Scout:Plugins:WorldClock". The plugin
/// self-gates on <c>Enabled</c> (default true): when disabled the call is a clean
/// no-op (no kernel plugin). When enabled it pulls in the shared foundation
/// (<see cref="TimeProvider"/>), registers the offline city resolver, and registers
/// a <see cref="KernelPlugin"/> the LLM can invoke via Semantic Kernel
/// function-calling.
/// </summary>
public static class WorldClockServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="WorldClockOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:WorldClock";

    /// <summary>
    /// Register the WorldClock plugin. No-op when <c>Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddWorldClockPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<WorldClockOptions>(section);

        var enabled = section.GetValue("Enabled", true);
        if (!enabled)
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);
        services.TryAddSingleton<ICityZoneResolver, CityZoneResolver>();

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WorldClockOptions>>().Value;
            var clock = sp.GetRequiredService<TimeProvider>();
            var resolver = sp.GetRequiredService<ICityZoneResolver>();
            // Empty config means "use the machine's local zone": the worker runs
            // on the user's own device, so its local time is the right default.
            var timeZone = string.IsNullOrWhiteSpace(options.DefaultTimeZone)
                ? TimeZoneInfo.Local.Id
                : options.DefaultTimeZone;
            return KernelPluginFactory.CreateFromObject(
                new WorldClockPlugin(clock, resolver, timeZone), "WorldClock");
        });

        return services;
    }
}
