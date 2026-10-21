using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;

namespace Scout.Plugin.Dates;

/// <summary>
/// DI composition for the read-only Dates plugin. Binds <see cref="DatesOptions"/>
/// from "Scout:Plugins:Dates". The plugin self-gates on <c>Enabled</c> (default
/// true): when disabled the call is a clean no-op (no kernel plugin). When enabled
/// it pulls in the shared foundation (<see cref="TimeProvider"/> and the local
/// store) and registers a <see cref="KernelPlugin"/> the LLM can invoke via
/// Semantic Kernel function-calling.
/// </summary>
public static class DatesServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="DatesOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Dates";

    /// <summary>
    /// Register the Dates plugin. No-op when <c>Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddDatesPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<DatesOptions>(section);

        var enabled = section.GetValue("Enabled", true);
        if (!enabled)
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DatesOptions>>().Value;
            var clock = sp.GetRequiredService<TimeProvider>();
            // Empty config means "use the machine's local zone": the worker runs
            // on the user's own device, so its local time is the right default.
            var timeZone = string.IsNullOrWhiteSpace(options.DefaultTimeZone)
                ? TimeZoneInfo.Local.Id
                : options.DefaultTimeZone;
            return KernelPluginFactory.CreateFromObject(
                new DatesPlugin(clock, timeZone), "Dates");
        });

        return services;
    }
}
