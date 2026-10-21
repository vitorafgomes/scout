namespace Scout.Plugin.Markets.Abstractions;

/// <summary>
/// A trending coin entry from CoinGecko's search/trending list. Read-only metadata
/// describing popularity only; no price is implied.
/// </summary>
/// <param name="Id">The CoinGecko coin id, e.g. "bitcoin".</param>
/// <param name="Name">The human-readable coin name, e.g. "Bitcoin".</param>
/// <param name="Symbol">The uppercase ticker symbol, e.g. "BTC".</param>
/// <param name="MarketCapRank">The market-cap rank when known; otherwise null.</param>
public sealed record TrendingCoin(
    string Id,
    string Name,
    string Symbol,
    int? MarketCapRank);
