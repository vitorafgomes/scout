namespace Scout.Plugin.Markets.Abstractions;

/// <summary>
/// Read-only access to delayed stock and index quotes. Every call is an HTTP GET;
/// no operation changes remote state and there is no trading surface.
/// </summary>
public interface IStockQuoteClient
{
    /// <summary>
    /// Fetch the latest delayed quote for <paramref name="symbol"/> (an uppercase
    /// ticker with a market suffix, e.g. "AAPL.US" or "PETR4.SA"). Returns null when
    /// the symbol is unknown or the source reports no data for it.
    /// </summary>
    Task<StockQuote?> GetQuoteAsync(string symbol, CancellationToken ct);
}
