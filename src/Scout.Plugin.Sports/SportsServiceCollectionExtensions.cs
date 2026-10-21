using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Sports.Abstractions;

namespace Scout.Plugin.Sports;

/// <summary>
/// DI composition for the read-only Sports plugin. Binds <see cref="SportsOptions"/>
/// from "Scout:Plugins:Sports". The plugin self-gates on the <c>Enabled</c> flag
/// (default true): when disabled the call is a clean no-op (no client, no plugin),
/// so an unconfigured host still starts. The underlying API works on a free public
/// test key baked into the base address; an optional <c>FootballDataToken</c>
/// upgrades that key path segment when present. When enabled it registers a typed
/// <see cref="ISportsClient"/> over a configured <see cref="HttpClient"/> and a
/// <see cref="KernelPlugin"/> the LLM can invoke via function-calling.
/// </summary>
public static class SportsServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="SportsOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Sports";

    private const string ApiBaseFormat = "https://www.thesportsdb.com/api/v1/json/{0}/";

    private const string FreeApiKey = "3";

    /// <summary>
    /// Register the Sports plugin. No-op when <c>Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddSportsPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<SportsOptions>(section);

        var enabled = section.GetValue("Enabled", true);
        if (!enabled)
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);

        // The free public test key ("3") lives in the base-address path segment; an
        // optional upgrade key replaces it without changing anything else.
        var token = section.GetValue<string>("FootballDataToken");
        var apiKey = string.IsNullOrWhiteSpace(token) ? FreeApiKey : token.Trim();
        var baseAddress = new Uri(string.Format(
            System.Globalization.CultureInfo.InvariantCulture, ApiBaseFormat, apiKey));

        services.AddHttpClient<ISportsClient, SportsClient>(client =>
        {
            client.BaseAddress = baseAddress;
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            // Some public APIs reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new SportsPlugin(
                sp.GetRequiredService<ISportsClient>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<SportsOptions>>().Value),
            "Sports"));

        return services;
    }
}
