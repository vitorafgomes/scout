using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Markets.Abstractions;

namespace Scout.Plugin.Markets;

/// <summary>
/// Read-only markets plugin exposed to the LLM as Semantic Kernel functions. Sources
/// delayed stock and index quotes from Stooq and delayed crypto prices from
/// CoinGecko. There is no trading, ordering, or account surface: every function is a
/// GET-backed lookup. Each call returns a non-empty, user-safe string; invalid model
/// input yields a corrective sentence and transport/API failures are caught and
/// surfaced as a clean error line, so a tool call never throws back into the kernel.
/// All quotes are explicitly labelled as delayed.
/// </summary>
public sealed class MarketsPlugin
{
    private const string InvalidSymbolMessage =
        "Provide a ticker symbol like AAPL.US or PETR4.SA.";
    private const string InvalidCoinMessage =
        "Provide a coin id like bitcoin or ethereum.";
    private const string InvalidCurrencyMessage =
        "Use a currency code like usd, eur, or brl.";
    private const string DelayedNote = "delayed quote";
    private const int MaxTrending = 7;
    private const int MaxNameLength = 60;

    private readonly IStockQuoteClient _stocks;
    private readonly ICryptoMarketClient _crypto;

    /// <summary>Creates the plugin over read-only stock and crypto clients.</summary>
    /// <param name="stocks">The Stooq stock-quote client. Must be non-null.</param>
    /// <param name="crypto">The CoinGecko crypto client. Must be non-null.</param>
    public MarketsPlugin(IStockQuoteClient stocks, ICryptoMarketClient crypto)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
    }

    /// <summary>The latest delayed quote for a stock or index.</summary>
    /// <param name="symbol">Ticker symbol with a market suffix.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>A one-line delayed quote, a not-found sentence, or a clean error.</returns>
    [KernelFunction("get_stock_quote")]
    [Description("Get the latest delayed quote for a stock or index by ticker symbol, including the last price and the day's move.")]
    public async Task<string> GetStockQuoteAsync(
        [Description("Ticker symbol with market suffix, e.g. AAPL.US or PETR4.SA.")] string symbol,
        CancellationToken ct = default)
    {
        var ticker = NormalizeSymbol(symbol);
        if (!IsValidSymbol(ticker))
        {
            return InvalidSymbolMessage;
        }

        try
        {
            var quote = await _stocks.GetQuoteAsync(ticker, ct).ConfigureAwait(false);
            return quote is null
                ? $"No quote found for {ticker}. Check the symbol, e.g. AAPL.US or PETR4.SA."
                : FormatQuote(quote);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex, "stock quote");
        }
    }

    /// <summary>The latest delayed price of a cryptocurrency in a chosen currency.</summary>
    /// <param name="coin">CoinGecko coin id.</param>
    /// <param name="currency">Currency code to price in.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>A one-line delayed price, a not-found sentence, or a clean error.</returns>
    [KernelFunction("get_crypto_price")]
    [Description("Get the latest delayed price of a cryptocurrency in a chosen fiat or crypto currency, with its 24-hour change when available.")]
    public async Task<string> GetCryptoPriceAsync(
        [Description("CoinGecko coin id, e.g. bitcoin or ethereum.")] string coin,
        [Description("Currency code to price in, e.g. usd, eur, or brl.")] string currency = "usd",
        CancellationToken ct = default)
    {
        var coinId = NormalizeCoin(coin);
        if (!IsValidCoin(coinId))
        {
            return InvalidCoinMessage;
        }

        var vs = NormalizeCurrency(currency);
        if (!IsValidCurrency(vs))
        {
            return InvalidCurrencyMessage;
        }

        try
        {
            var price = await _crypto.GetPriceAsync(coinId, vs, ct).ConfigureAwait(false);
            return price is null
                ? $"No price found for {coinId} in {vs.ToUpperInvariant()}. Use a CoinGecko id like bitcoin."
                : FormatPrice(price);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex, "crypto price");
        }
    }

    /// <summary>The cryptocurrencies currently trending on CoinGecko.</summary>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The trending coins, one per line, or a clean message.</returns>
    [KernelFunction("get_trending_crypto")]
    [Description("List the cryptocurrencies currently trending (most searched) on CoinGecko, most popular first.")]
    public async Task<string> GetTrendingCryptoAsync(CancellationToken ct = default)
    {
        try
        {
            var coins = await _crypto.GetTrendingAsync(ct).ConfigureAwait(false);
            if (coins.Count == 0)
            {
                return "No trending cryptocurrencies are available right now.";
            }

            var lines = coins.Take(MaxTrending).Select(FormatTrending);
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex, "trending crypto");
        }
    }

    private static string FormatQuote(StockQuote quote)
    {
        var when = quote.Time == "-" ? Iso(quote.Date) : $"{Iso(quote.Date)} {quote.Time}";
        if (quote.Open > 0m)
        {
            var change = quote.Close - quote.Open;
            var pct = change / quote.Open * 100m;
            return $"{quote.Symbol}: {Num(quote.Close)} ({Signed(change)}, {Signed(pct)}%) "
                + $"as of {when} - {DelayedNote}";
        }

        return $"{quote.Symbol}: {Num(quote.Close)} as of {when} - {DelayedNote}";
    }

    private static string FormatPrice(CryptoPrice price)
    {
        var head = $"{price.Coin} = {Num(price.Price)} {price.Currency.ToUpperInvariant()}";
        return price.Change24h is { } change
            ? $"{head} ({Signed(change)}% 24h) - {DelayedNote}"
            : $"{head} - {DelayedNote}";
    }

    private static string FormatTrending(TrendingCoin coin, int index)
    {
        var rank = coin.MarketCapRank is { } value
            ? $" - market cap rank {value.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        return $"{index + 1}. {Clip(coin.Name)} ({coin.Symbol}){rank}";
    }

    private static string NormalizeSymbol(string? symbol)
        => (symbol ?? string.Empty).Trim().ToUpperInvariant();

    private static bool IsValidSymbol(string symbol)
        => symbol.Length is > 0 and <= 20
            && symbol.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '^');

    private static string NormalizeCoin(string? coin)
        => (coin ?? string.Empty).Trim().ToLowerInvariant();

    private static bool IsValidCoin(string coin)
        => coin.Length is > 0 and <= 40
            && coin.All(c => char.IsAsciiLetterOrDigit(c) || c is '-');

    private static string NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? "usd" : currency.Trim().ToLowerInvariant();

    private static bool IsValidCurrency(string currency)
        => currency.Length is >= 2 and <= 5 && currency.All(char.IsAsciiLetter);

    private static string Clip(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= MaxNameLength ? flat : flat[..MaxNameLength];
    }

    private static string Num(decimal value)
        => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string Signed(decimal value)
    {
        var formatted = Math.Abs(value).ToString("0.##", CultureInfo.InvariantCulture);
        return value >= 0m ? $"+{formatted}" : $"-{formatted}";
    }

    private static string Iso(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool IsHandledFailure(Exception ex)
        => ex is MarketsApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex, string subject) => ex switch
    {
        MarketsApiException api => api.Message,
        OperationCanceledException => $"The {subject} request timed out. Try again later.",
        _ => $"Could not reach the {subject} service. Try again later.",
    };
}
