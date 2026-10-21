using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Wikipedia.Abstractions;

namespace Scout.Plugin.Wikipedia;

/// <summary>
/// DI composition for the read-only Wikipedia plugin. Binds
/// <see cref="WikipediaOptions"/> from "Scout:Plugins:Wikipedia". The Wikimedia
/// REST API needs no key, so the plugin self-gates on an <c>Enabled</c> flag
/// (default true): when disabled the call is a clean no-op (no client, no plugin).
/// When enabled it registers a typed <see cref="IWikipediaClient"/> over a
/// configured <see cref="HttpClient"/> and a <see cref="KernelPlugin"/> the LLM
/// can invoke via Semantic Kernel function-calling.
/// </summary>
public static class WikipediaServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="WikipediaOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Wikipedia";

    private const string ApiBaseAddress = "https://api.wikimedia.org/";

    /// <summary>
    /// Register the Wikipedia plugin. No-op when the section sets Enabled to false.
    /// </summary>
    public static IServiceCollection AddWikipediaPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<WikipediaOptions>(section);

        var enabled = section.GetValue<bool?>("Enabled") ?? true;
        if (!enabled)
        {
            return services;
        }

        services.AddHttpClient<IWikipediaClient, WikipediaClient>(client =>
        {
            client.BaseAddress = new Uri(ApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            // Wikimedia APIs reject requests that omit a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new WikipediaPlugin(sp.GetRequiredService<IWikipediaClient>()), "Wikipedia"));

        return services;
    }
}
