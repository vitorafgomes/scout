namespace Scout.Plugin.Markets.Abstractions;

/// <summary>
/// A single delayed cryptocurrency spot price from CoinGecko, expressed in a chosen
/// vs-currency. Read-only projection.
/// </summary>
/// <param name="Coin">The CoinGecko coin id, e.g. "bitcoin".</param>
/// <param name="Currency">The lowercase vs-currency code the price is quoted in, e.g. "usd".</param>
/// <param name="Price">The latest price of one coin in the vs-currency.</param>
/// <param name="Change24h">The 24-hour percentage change when the source reports it; otherwise null.</param>
public sealed record CryptoPrice(
    string Coin,
    string Currency,
    decimal Price,
    decimal? Change24h);
