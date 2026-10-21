using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.WebSearch.Abstractions;

namespace Scout.Plugin.WebSearch;

/// <summary>
/// DI composition for the read-only WebSearch plugin. Binds
/// <see cref="WebSearchOptions"/> from "Scout:Plugins:WebSearch". The plugin
/// self-gates on <c>Enabled</c> (default true): when disabled the call is a clean
/// no-op (no client, no plugin), so an unconfigured host still starts. It also
/// gates on <c>SearxUrl</c>: with a blank value the plugin still registers but the
/// SearXNG-backed <c>search_web</c> function is dropped, leaving only the
/// zero-setup DuckDuckGo instant-answer and page-fetch functions. When enabled it
/// registers a typed <see cref="IWebSearchClient"/> over a configured
/// <see cref="HttpClient"/> and a <see cref="KernelPlugin"/> the LLM can invoke via
/// Semantic Kernel function-calling.
/// </summary>
public static class WebSearchServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="WebSearchOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:WebSearch";

    private const string SearchWebFunctionName = "search_web";

    /// <summary>
    /// Register the WebSearch plugin. No-op when the section sets Enabled to false.
    /// </summary>
    public static IServiceCollection AddWebSearchPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<WebSearchOptions>(section);

        var enabled = section.GetValue<bool?>("Enabled") ?? true;
        if (!enabled)
        {
            return services;
        }

        services.AddHttpClient<IWebSearchClient, WebSearchClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            // Some servers reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WebSearchOptions>>().Value;
            var plugin = new WebSearchPlugin(sp.GetRequiredService<IWebSearchClient>(), options);
            var full = KernelPluginFactory.CreateFromObject(plugin, "WebSearch");

            if (!string.IsNullOrWhiteSpace(options.SearxUrl))
            {
                return full;
            }

            // No SearXNG instance: gate down to the zero-setup DuckDuckGo surface by
            // dropping search_web from the exposed function set.
            var reduced = full.Where(f => f.Name != SearchWebFunctionName);
            return KernelPluginFactory.CreateFromFunctions("WebSearch", reduced);
        });

        return services;
    }
}
