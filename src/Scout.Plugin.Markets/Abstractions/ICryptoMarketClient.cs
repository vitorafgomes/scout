namespace Scout.Plugin.Markets.Abstractions;

/// <summary>
/// Read-only access to delayed cryptocurrency market data from CoinGecko. Every
/// call is an HTTP GET; no operation changes remote state and there is no trading
/// surface.
/// </summary>
public interface ICryptoMarketClient
{
    /// <summary>
    /// Fetch the latest delayed price of <paramref name="coin"/> (a CoinGecko id such
    /// as "bitcoin") in <paramref name="currency"/> (a lowercase vs-currency code such
    /// as "usd"). Returns null when the coin or currency is unknown.
    /// </summary>
    Task<CryptoPrice?> GetPriceAsync(string coin, string currency, CancellationToken ct);

    /// <summary>
    /// List the coins currently trending (most searched) on CoinGecko, most popular
    /// first. Returns an empty list when nothing is trending.
    /// </summary>
    Task<IReadOnlyList<TrendingCoin>> GetTrendingAsync(CancellationToken ct);
}
