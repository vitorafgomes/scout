using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Holidays.Abstractions;

namespace Scout.Plugin.Holidays;

/// <summary>
/// DI composition for the read-only Holidays plugin. Binds
/// <see cref="HolidaysOptions"/> from "Scout:Plugins:Holidays". The plugin
/// self-gates on <c>Enabled</c> (default true): when disabled the call is a clean
/// no-op (no client, no plugin), so an unconfigured host still starts. When
/// enabled it registers a typed <see cref="IHolidaysClient"/> over a configured
/// <see cref="HttpClient"/> and a <see cref="KernelPlugin"/> the LLM can invoke via
/// Semantic Kernel function-calling.
/// </summary>
public static class HolidaysServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="HolidaysOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Holidays";

    private const string ApiBaseAddress = "https://date.nager.at/";

    /// <summary>
    /// Register the Holidays plugin. No-op when the section sets Enabled to false.
    /// </summary>
    public static IServiceCollection AddHolidaysPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<HolidaysOptions>(section);

        var enabled = section.GetValue<bool?>("Enabled") ?? true;
        if (!enabled)
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);

        services.AddHttpClient<IHolidaysClient, HolidaysClient>(client =>
        {
            client.BaseAddress = new Uri(ApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            // Some public APIs reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new HolidaysPlugin(
                sp.GetRequiredService<IHolidaysClient>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<HolidaysOptions>>().Value),
            "Holidays"));

        return services;
    }
}
