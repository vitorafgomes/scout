using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Markets.Abstractions;

namespace Scout.Plugin.Markets;

/// <summary>
/// DI composition for the read-only Markets plugin. Binds <see cref="MarketsOptions"/>
/// from "Scout:Plugins:Markets". The plugin self-gates on the <c>Enabled</c> flag
/// (default true): when disabled the call is a clean no-op (no clients, no plugin),
/// so an unconfigured host still starts. When enabled it registers a typed
/// <see cref="IStockQuoteClient"/> (Stooq) and <see cref="ICryptoMarketClient"/>
/// (CoinGecko) over configured <see cref="HttpClient"/> instances and a
/// <see cref="KernelPlugin"/> the LLM can invoke via Semantic Kernel
/// function-calling. Both APIs are keyless; an optional CoinGecko demo key only
/// raises the rate limit.
/// </summary>
public static class MarketsServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="MarketsOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Markets";

    private const string StooqBaseAddress = "https://stooq.com/";
    private const string CoinGeckoBaseAddress = "https://api.coingecko.com/";

    /// <summary>
    /// Register the Markets plugin. No-op when <c>Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddMarketsPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<MarketsOptions>(section);

        var enabled = section.GetValue("Enabled", true);
        if (!enabled)
        {
            return services;
        }

        // The CoinGecko client depends on the shared TimeProvider for its cache
        // and throttle.
        services.AddScoutPluginCommon(configuration);

        var apiKey = section.GetValue<string>("CoinGeckoApiKey");

        services.AddHttpClient<IStockQuoteClient, StooqClient>(client =>
        {
            client.BaseAddress = new Uri(StooqBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            // Some public APIs reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        services.AddHttpClient<ICryptoMarketClient, CoinGeckoClient>(client =>
        {
            client.BaseAddress = new Uri(CoinGeckoBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            // Some public APIs reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                // Optional demo key only raises the shared rate limit.
                client.DefaultRequestHeaders.Add("x-cg-demo-api-key", apiKey);
            }
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new MarketsPlugin(
                sp.GetRequiredService<IStockQuoteClient>(),
                sp.GetRequiredService<ICryptoMarketClient>()),
            "Markets"));

        return services;
    }
}
