using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.WhatToWatch.Abstractions;

namespace Scout.Plugin.WhatToWatch;

/// <summary>
/// DI composition for the read-only WhatToWatch plugin. Binds
/// <see cref="WhatToWatchOptions"/> from "Scout:Plugins:WhatToWatch". The plugin
/// self-gates on <c>Enabled</c> (default true): when disabled the call is a clean
/// no-op (no clients, no plugin), so an unconfigured host still starts. When
/// enabled it always registers the free TVMaze client and the kernel plugin; the
/// TMDB client backing <c>where_to_watch</c> is registered only when a TMDB key is
/// configured, and the function degrades to a friendly hint otherwise.
/// </summary>
public static class WhatToWatchServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="WhatToWatchOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:WhatToWatch";

    private const string TvMazeBaseAddress = "https://api.tvmaze.com/";
    private const string TmdbBaseAddress = "https://api.themoviedb.org/";

    /// <summary>
    /// Register the WhatToWatch plugin. No-op when the section sets Enabled to
    /// false. The TMDB client is added only when a TMDB key is present.
    /// </summary>
    public static IServiceCollection AddWhatToWatchPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<WhatToWatchOptions>(section);

        var enabled = section.GetValue<bool?>("Enabled") ?? true;
        if (!enabled)
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);

        services.AddHttpClient<ITvMazeClient, TvMazeClient>(client =>
        {
            client.BaseAddress = new Uri(TvMazeBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            // Some public APIs reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        var tmdbKey = section.GetValue<string>("TmdbApiKey");
        if (!string.IsNullOrWhiteSpace(tmdbKey))
        {
            // The key is not resolvable positionally by DI, so build the typed
            // client through a factory once the HttpClient is configured.
            services.AddHttpClient<ITmdbClient, TmdbClient>(client =>
            {
                client.BaseAddress = new Uri(TmdbBaseAddress);
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
            })
            .AddTypedClient<ITmdbClient>((http, _) => new TmdbClient(http, tmdbKey));
        }

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new WhatToWatchPlugin(
                sp.GetRequiredService<ITvMazeClient>(),
                sp.GetService<ITmdbClient>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<WhatToWatchOptions>>().Value),
            "WhatToWatch"));

        return services;
    }
}
