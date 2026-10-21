using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.News.Abstractions;

namespace Scout.Plugin.News;

/// <summary>
/// DI composition for the News plugin. Binds <see cref="NewsOptions"/> from
/// "Scout:Plugins:News". The plugin self-gates on <c>Enabled</c> (default true):
/// when disabled the call is a clean no-op (no clients, no store, no plugin), so an
/// unconfigured host still starts. When enabled it registers a typed
/// <see cref="IFeedClient"/> and <see cref="IHackerNewsClient"/> over configured
/// <see cref="HttpClient"/>s, the shared Scout-local store, and a
/// <see cref="KernelPlugin"/> the LLM can invoke via Semantic Kernel
/// function-calling.
/// </summary>
public static class NewsServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="NewsOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:News";

    private const string HackerNewsBaseAddress = "https://hn.algolia.com/";

    /// <summary>
    /// Register the News plugin. No-op when the section sets Enabled to false.
    /// </summary>
    public static IServiceCollection AddNewsPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<NewsOptions>(section);

        var enabled = section.GetValue<bool?>("Enabled") ?? true;
        if (!enabled)
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);

        // Feed URLs are absolute (from the alias map), so this client has no base
        // address; it fetches each configured feed URL directly.
        services.AddHttpClient<IFeedClient, FeedClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            // Some feed hosts reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/atom+xml");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");
        });

        services.AddHttpClient<IHackerNewsClient, HackerNewsClient>(client =>
        {
            client.BaseAddress = new Uri(HackerNewsBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new NewsPlugin(
                sp.GetRequiredService<IFeedClient>(),
                sp.GetRequiredService<IHackerNewsClient>(),
                sp.GetRequiredService<IScoutLocalStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<NewsOptions>>().Value),
            "News"));

        return services;
    }
}
