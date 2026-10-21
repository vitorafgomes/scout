using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.Currency.Abstractions;

namespace Scout.Plugin.Currency;

/// <summary>
/// Thin read-only client over the Frankfurter API (ECB reference rates) using an
/// injected, pre-configured <see cref="HttpClient"/> (base address, timeout, and
/// User-Agent are set by DI). Non-success responses and unreadable bodies become
/// a user-safe <see cref="CurrencyApiException"/>; timeouts and transport faults
/// bubble unchanged. Every request is a GET; nothing mutates remote state.
/// </summary>
internal sealed class CurrencyClient(HttpClient http) : ICurrencyClient
{
    private const int TrendWindowDays = 30;

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<ExchangeRate?> GetLatestRateAsync(
        string baseCode, string targetCode, CancellationToken ct)
    {
        var dto = await GetAsync<LatestDto>(
            $"v1/latest?base={baseCode}&symbols={targetCode}", ct).ConfigureAwait(false);

        if (dto?.Rates is null || !dto.Rates.TryGetValue(targetCode, out var rate))
        {
            return null;
        }

        return new ExchangeRate(
            Base: string.IsNullOrWhiteSpace(dto.Base) ? baseCode : dto.Base,
            Target: targetCode,
            Rate: rate,
            Date: ParseDate(dto.Date));
    }

    public async Task<RateTrend?> GetTrendAsync(
        string baseCode, string targetCode, CancellationToken ct)
    {
        // The window is computed in code: a plain UTC "today" is adequate for a
        // day-resolution ECB series.
        var end = DateTime.UtcNow.Date;
        var start = end.AddDays(-TrendWindowDays);
        var path =
            $"v1/{start:yyyy-MM-dd}..{end:yyyy-MM-dd}?base={baseCode}&symbols={targetCode}";

        var dto = await GetAsync<SeriesDto>(path, ct).ConfigureAwait(false);
        if (dto?.Rates is null || dto.Rates.Count == 0)
        {
            return null;
        }

        var points = dto.Rates
            .Where(kvp => kvp.Value is not null && kvp.Value.ContainsKey(targetCode))
            .Select(kvp => (Date: ParseDate(kvp.Key), Rate: kvp.Value![targetCode]))
            .OrderBy(p => p.Date)
            .ToList();
        if (points.Count == 0)
        {
            return null;
        }

        return new RateTrend(
            Base: string.IsNullOrWhiteSpace(dto.Base) ? baseCode : dto.Base,
            Target: targetCode,
            Low: points.Min(p => p.Rate),
            High: points.Max(p => p.Rate),
            First: points[0].Rate,
            Last: points[^1].Rate,
            StartDate: points[0].Date,
            EndDate: points[^1].Date);
    }

    /// <summary>
    /// GET that returns null on 404 (unknown currency / no rate) and maps other
    /// non-success responses and unreadable bodies to
    /// <see cref="CurrencyApiException"/>. Transport faults and timeouts bubble
    /// unchanged.
    /// </summary>
    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        try
        {
            return await response.Content
                .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new CurrencyApiException(
                "The exchange rate service returned an unreadable response. Try again later.");
        }
    }

    private static CurrencyApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests)
        {
            return new CurrencyApiException(
                "The exchange rate service is rate limiting requests. Try again shortly.", status);
        }

        if ((int)status >= 500)
        {
            return new CurrencyApiException(
                "The exchange rate service is currently unavailable. Try again later.", status);
        }

        return new CurrencyApiException(
            $"The exchange rate request failed ({(int)status}).", status);
    }

    private static DateOnly ParseDate(string? value)
        => DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record LatestDto(
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("base")] string? Base,
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("rates")] Dictionary<string, decimal>? Rates);

    private sealed record SeriesDto(
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("base")] string? Base,
        [property: JsonPropertyName("start_date")] string? StartDate,
        [property: JsonPropertyName("end_date")] string? EndDate,
        [property: JsonPropertyName("rates")] Dictionary<string, Dictionary<string, decimal>>? Rates);
}
