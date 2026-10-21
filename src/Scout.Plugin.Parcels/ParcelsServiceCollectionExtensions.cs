using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Parcels.Abstractions;

namespace Scout.Plugin.Parcels;

/// <summary>
/// DI composition for the Parcels plugin. Binds <see cref="ParcelsOptions"/> from
/// "Scout:Plugins:Parcels". The plugin self-gates on the API key: when no key is
/// configured the call is a clean no-op (no client, no plugin), so an
/// unconfigured host still starts. When a key is present it registers a typed
/// <see cref="IParcelsClient"/> over a configured <see cref="HttpClient"/>, the
/// shared Scout-local store for the watch list, and a <see cref="KernelPlugin"/>
/// the LLM can invoke via Semantic Kernel function-calling.
/// </summary>
public static class ParcelsServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="ParcelsOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Parcels";

    private const string ApiBaseAddress = "https://api.ship24.com/";

    /// <summary>
    /// Register the Parcels plugin. No-op when the API key is empty or whitespace.
    /// </summary>
    public static IServiceCollection AddParcelsPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<ParcelsOptions>(section);

        var apiKey = section.GetValue<string>("ApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);

        services.AddHttpClient<IParcelsClient, ParcelsClient>(client =>
        {
            client.BaseAddress = new Uri(ApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new ParcelsPlugin(
                sp.GetRequiredService<IParcelsClient>(),
                sp.GetRequiredService<IScoutLocalStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<ParcelsOptions>>().Value),
            "Parcels"));

        return services;
    }
}
