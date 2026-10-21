namespace Scout.Plugin.Markets;

/// <summary>
/// Configuration for the Markets plugin, bound from "Scout:Plugins:Markets". Both
/// data sources (Stooq for stocks/indexes, CoinGecko for crypto) are free and
/// keyless, so the plugin is on by default and self-gates only on
/// <see cref="Enabled"/>. An optional <see cref="CoinGeckoApiKey"/> raises the
/// CoinGecko rate limit but is never required.
/// </summary>
public sealed class MarketsOptions
{
    /// <summary>
    /// Whether the plugin is active. When false the registration is a clean no-op
    /// (no clients, no kernel plugin). Defaults to true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional CoinGecko demo API key. When set it is sent as the
    /// <c>x-cg-demo-api-key</c> header to raise the shared rate limit. Empty (the
    /// default) uses the anonymous public tier.
    /// </summary>
    public string CoinGeckoApiKey { get; set; } = string.Empty;
}
