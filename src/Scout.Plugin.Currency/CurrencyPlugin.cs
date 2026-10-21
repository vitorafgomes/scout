using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Currency.Abstractions;

namespace Scout.Plugin.Currency;

/// <summary>
/// Read-only currency plugin exposed to the LLM as Semantic Kernel functions.
/// Sources ECB reference rates from the Frankfurter API (no key). Every call
/// returns a non-empty, user-safe string; invalid model input yields a
/// corrective sentence and transport/API failures are caught and surfaced as a
/// clean error line, so a tool call never throws back into the kernel.
/// </summary>
public sealed class CurrencyPlugin
{
    private const string InvalidCodeMessage =
        "That does not look like a valid currency code. Use ISO codes like GBP, BRL, EUR.";

    private const string InvalidAmountMessage =
        "Could not read the amount. Provide a positive number like 250 or 250.50.";

    private readonly ICurrencyClient _client;

    /// <summary>Creates the plugin over a read-only currency client.</summary>
    /// <param name="client">The currency client. Must be non-null.</param>
    public CurrencyPlugin(ICurrencyClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Get the current ECB rate between two currencies.</summary>
    /// <param name="from">Source currency ISO code.</param>
    /// <param name="to">Target currency ISO code.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>A one-line rate summary, or a clean message when unavailable or on failure.</returns>
    [KernelFunction("get_exchange_rate")]
    [Description("Get the current ECB exchange rate between two currencies, i.e. how much of the target currency one unit of the source currency buys.")]
    public async Task<string> GetExchangeRateAsync(
        [Description("Source currency ISO code, e.g. GBP.")] string from,
        [Description("Target currency ISO code, e.g. BRL.")] string to,
        CancellationToken ct)
    {
        var fromCode = Normalize(from);
        var toCode = Normalize(to);
        if (!IsValidCode(fromCode) || !IsValidCode(toCode))
        {
            return InvalidCodeMessage;
        }

        try
        {
            var rate = await _client.GetLatestRateAsync(fromCode, toCode, ct).ConfigureAwait(false);
            return rate is null
                ? $"No ECB rate available for {fromCode} to {toCode}."
                : $"1 {rate.Base} = {FormatRate(rate.Rate)} {rate.Target} (ECB {FormatDate(rate.Date)})";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Convert an amount from one currency to another at the current ECB rate.</summary>
    /// <param name="amount">Amount to convert; formatted numbers are accepted.</param>
    /// <param name="from">Source currency ISO code.</param>
    /// <param name="to">Target currency ISO code.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The converted amount with the rate used, or a corrective/clean message.</returns>
    [KernelFunction("convert_currency")]
    [Description("Convert an amount of money from one currency to another using the current ECB rate. Returns the converted amount, the rate used, and its ECB date.")]
    public async Task<string> ConvertCurrencyAsync(
        [Description("Amount to convert; plain or formatted numbers are accepted, e.g. 250, 250.5, or £250.")] string amount,
        [Description("Source currency ISO code, e.g. GBP.")] string from,
        [Description("Target currency ISO code, e.g. BRL.")] string to,
        CancellationToken ct)
    {
        var fromCode = Normalize(from);
        var toCode = Normalize(to);
        if (!IsValidCode(fromCode) || !IsValidCode(toCode))
        {
            return InvalidCodeMessage;
        }

        if (!TryParseAmount(amount, out var value))
        {
            return InvalidAmountMessage;
        }

        try
        {
            var rate = await _client.GetLatestRateAsync(fromCode, toCode, ct).ConfigureAwait(false);
            if (rate is null)
            {
                return $"No ECB rate available for {fromCode} to {toCode}.";
            }

            var converted = value * rate.Rate;
            return $"{FormatAmount(value)} {rate.Base} = {FormatAmount(converted)} {rate.Target} "
                + $"(rate 1 {rate.Base} = {FormatRate(rate.Rate)} {rate.Target}, ECB {FormatDate(rate.Date)})";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Summarize the 30-day trend of an exchange rate.</summary>
    /// <param name="from">Source currency ISO code.</param>
    /// <param name="to">Target currency ISO code.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>A low/high plus strengthened/weakened summary, or a clean message.</returns>
    [KernelFunction("get_rate_trend")]
    [Description("Summarize the last 30 days of an exchange rate: its low, its high, and whether the source currency strengthened or weakened against the target.")]
    public async Task<string> GetRateTrendAsync(
        [Description("Source currency ISO code, e.g. GBP.")] string from,
        [Description("Target currency ISO code, e.g. BRL.")] string to,
        CancellationToken ct)
    {
        var fromCode = Normalize(from);
        var toCode = Normalize(to);
        if (!IsValidCode(fromCode) || !IsValidCode(toCode))
        {
            return InvalidCodeMessage;
        }

        try
        {
            var trend = await _client.GetTrendAsync(fromCode, toCode, ct).ConfigureAwait(false);
            return trend is null
                ? $"No ECB rate history available for {fromCode} to {toCode}."
                : FormatTrend(trend);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string FormatTrend(RateTrend trend)
    {
        var direction = trend.Last > trend.First
            ? "strengthened"
            : trend.Last < trend.First
                ? "weakened"
                : "was unchanged";

        return $"{trend.Base}/{trend.Target} over {FormatDate(trend.StartDate)} to {FormatDate(trend.EndDate)}: "
            + $"low {FormatRate(trend.Low)}, high {FormatRate(trend.High)}. "
            + $"{trend.Base} {direction} against {trend.Target} "
            + $"(from {FormatRate(trend.First)} to {FormatRate(trend.Last)}).";
    }

    private static string Normalize(string? code)
        => (code ?? string.Empty).Trim().ToUpperInvariant();

    private static bool IsValidCode(string code)
        => code.Length == 3 && code.All(char.IsAsciiLetter);

    /// <summary>
    /// Parse an amount defensively. Accepts plain numbers, currency symbols, and
    /// either separator convention (e.g. "250", "250.5", "250,5", "£250",
    /// "R$ 120"). Currency symbols, letters, and whitespace are stripped; the
    /// numeric core is normalized to InvariantCulture before parsing.
    /// </summary>
    private static bool TryParseAmount(string? raw, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // Keep only digits, separators, and a leading sign; drop symbols/letters.
        var filtered = new string(raw
            .Where(c => char.IsDigit(c) || c is '.' or ',' or '-')
            .ToArray());
        if (filtered.Length == 0)
        {
            return false;
        }

        var hasDot = filtered.Contains('.');
        var hasComma = filtered.Contains(',');

        string normalized;
        if (hasDot && hasComma)
        {
            // The rightmost separator is the decimal point; the other is grouping.
            normalized = filtered.LastIndexOf(',') > filtered.LastIndexOf('.')
                ? filtered.Replace(".", string.Empty).Replace(',', '.')
                : filtered.Replace(",", string.Empty);
        }
        else if (hasComma)
        {
            // A lone comma is treated as the decimal separator, e.g. "250,5".
            normalized = filtered.Replace(',', '.');
        }
        else
        {
            normalized = filtered;
        }

        return decimal.TryParse(
                normalized,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out amount)
            && amount > 0m;
    }

    private static string FormatRate(decimal rate)
        => rate.ToString(CultureInfo.InvariantCulture);

    private static string FormatAmount(decimal amount)
        => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatDate(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool IsHandledFailure(Exception ex)
        => ex is CurrencyApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        CurrencyApiException api => api.Message,
        OperationCanceledException => "The exchange rate request timed out. Try again later.",
        _ => "Could not reach the exchange rate service. Try again later.",
    };
}
