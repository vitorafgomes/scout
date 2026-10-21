namespace Scout.Plugin.Currency.Abstractions;

/// <summary>
/// Read-only access to fiat exchange rates from the ECB reference set. Every
/// call is an HTTP GET; no operation changes remote state.
/// </summary>
public interface ICurrencyClient
{
    /// <summary>
    /// Fetch the latest ECB rate from <paramref name="baseCode"/> to
    /// <paramref name="targetCode"/>. Returns null when no rate exists for the
    /// pair (unknown or unsupported currency).
    /// </summary>
    Task<ExchangeRate?> GetLatestRateAsync(string baseCode, string targetCode, CancellationToken ct);

    /// <summary>
    /// Summarize the last 30 days of the rate from <paramref name="baseCode"/> to
    /// <paramref name="targetCode"/>. Returns null when the series is empty
    /// (unknown or unsupported currency).
    /// </summary>
    Task<RateTrend?> GetTrendAsync(string baseCode, string targetCode, CancellationToken ct);
}
