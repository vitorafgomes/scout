using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Weather.Abstractions;

namespace Scout.Plugin.Weather;

/// <summary>
/// DI composition for the read-only Weather plugin. Binds
/// <see cref="WeatherOptions"/> from "Scout:Plugins:Weather". The plugin
/// self-gates on the Enabled flag (default true): when disabled the call is a
/// clean no-op (no client, no plugin), so a host that opts out still starts. When
/// enabled it registers a typed <see cref="IWeatherClient"/> over a configured
/// <see cref="HttpClient"/> and a <see cref="KernelPlugin"/> the LLM can invoke
/// via Semantic Kernel function-calling.
/// </summary>
public static class WeatherServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="WeatherOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Weather";

    /// <summary>
    /// Register the Weather plugin. No-op when the Enabled flag is set to false.
    /// </summary>
    public static IServiceCollection AddWeatherPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<WeatherOptions>(section);

        var enabled = section.GetValue<bool?>("Enabled") ?? true;
        if (!enabled)
        {
            return services;
        }

        // RelaxedDate uses an injected TimeProvider; the plugin depends on it.
        services.AddScoutPluginCommon(configuration);

        services.AddHttpClient<IWeatherClient, WeatherClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            // Some Open-Meteo edges reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new WeatherPlugin(
                sp.GetRequiredService<IWeatherClient>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<WeatherOptions>>()),
            "Weather"));

        return services;
    }
}
