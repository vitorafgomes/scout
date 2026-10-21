using System.Globalization;
using System.Net;
using Scout.Plugin.Markets.Abstractions;

namespace Scout.Plugin.Markets;

/// <summary>
/// Thin read-only client over the Stooq light CSV quote endpoint using an injected,
/// pre-configured <see cref="HttpClient"/> (base address, timeout, and User-Agent
/// are set by DI). Non-success responses and unreadable bodies become a user-safe
/// <see cref="MarketsApiException"/>; timeouts and transport faults bubble
/// unchanged. Every request is a GET; nothing mutates remote state.
/// </summary>
internal sealed class StooqClient(HttpClient http) : IStockQuoteClient
{
    // Stooq marks unknown symbols and missing columns with this sentinel.
    private const string NoData = "N/D";
    private const string Unreadable =
        "The market data service returned an unreadable response. Try again later.";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<StockQuote?> GetQuoteAsync(string symbol, CancellationToken ct)
    {
        // Light quote endpoint: fields are symbol,date,time,open,high,low,close,volume.
        var path = $"q/l/?s={Uri.EscapeDataString(symbol)}&f=sd2t2ohlcv&e=csv";
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        var csv = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseQuote(csv);
    }

    private static StockQuote? ParseQuote(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }

        // Skip an optional header row; take the first data line.
        var line = csv
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return null;
        }

        var fields = line.Split(',');
        if (fields.Length < 8)
        {
            throw new MarketsApiException(Unreadable);
        }

        var symbol = fields[0].Trim();
        var date = fields[1].Trim();
        var time = fields[2].Trim();
        var close = fields[6].Trim();

        // A "N/D" date or close marks an unknown symbol: treat as "not found".
        if (IsNoData(date) || IsNoData(close))
        {
            return null;
        }

        if (!DateOnly.TryParseExact(
                date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var quoteDate)
            || !TryDecimal(close, out var closeValue))
        {
            throw new MarketsApiException(Unreadable);
        }

        return new StockQuote(
            Symbol: symbol.Length == 0 ? "?" : symbol,
            Date: quoteDate,
            Time: IsNoData(time) || time.Length == 0 ? "-" : time,
            Open: TryDecimal(fields[3], out var open) ? open : 0m,
            High: TryDecimal(fields[4], out var high) ? high : 0m,
            Low: TryDecimal(fields[5], out var low) ? low : 0m,
            Close: closeValue,
            Volume: long.TryParse(
                fields[7].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume)
                ? volume
                : 0L);
    }

    private static bool IsNoData(string value)
        => string.Equals(value, NoData, StringComparison.OrdinalIgnoreCase);

    private static bool TryDecimal(string raw, out decimal value)
        => decimal.TryParse(
            raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static MarketsApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests)
        {
            return new MarketsApiException(
                "The market data service is rate limiting requests. Try again shortly.", status);
        }

        if ((int)status >= 500)
        {
            return new MarketsApiException(
                "The market data service is currently unavailable. Try again later.", status);
        }

        return new MarketsApiException(
            $"The market data request failed ({(int)status}).", status);
    }
}
