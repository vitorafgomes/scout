using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.Markets.Abstractions;

namespace Scout.Plugin.Markets;

/// <summary>
/// Thin read-only client over the public CoinGecko API using an injected,
/// pre-configured <see cref="HttpClient"/> (base address, timeout, User-Agent, and
/// an optional demo key header are set by DI). Adds a small short-TTL in-memory
/// cache and a polite request throttle (~30 requests/minute) so repeated lookups do
/// not hammer the shared public tier. Non-success responses and unreadable bodies
/// become a user-safe <see cref="MarketsApiException"/>; timeouts and transport
/// faults bubble unchanged. Every request is a GET; nothing mutates remote state.
/// </summary>
internal sealed class CoinGeckoClient : ICryptoMarketClient
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    // ~30 requests/minute: keep at least this gap between outbound calls.
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;

    public CoinGeckoClient(HttpClient http, TimeProvider clock)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<CryptoPrice?> GetPriceAsync(string coin, string currency, CancellationToken ct)
    {
        var key = $"price:{coin}:{currency}";
        return await GetOrFetchAsync(
                key,
                async c => await FetchPriceAsync(coin, currency, c).ConfigureAwait(false),
                ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrendingCoin>> GetTrendingAsync(CancellationToken ct)
    {
        var cached = await GetOrFetchAsync<IReadOnlyList<TrendingCoin>>(
                "trending",
                async c => await FetchTrendingAsync(c).ConfigureAwait(false),
                ct)
            .ConfigureAwait(false);
        return cached ?? [];
    }

    private async Task<CryptoPrice?> FetchPriceAsync(string coin, string currency, CancellationToken ct)
    {
        var path =
            $"api/v3/simple/price?ids={Uri.EscapeDataString(coin)}"
            + $"&vs_currencies={Uri.EscapeDataString(currency)}&include_24hr_change=true";

        var map = await GetAsync<Dictionary<string, Dictionary<string, decimal>>>(path, ct)
            .ConfigureAwait(false);

        // An unknown coin yields an empty object; an unknown currency yields an
        // entry without the requested key. Both mean "no price".
        if (map is null
            || !map.TryGetValue(coin, out var values)
            || values is null
            || !values.TryGetValue(currency, out var price))
        {
            return null;
        }

        var change = values.TryGetValue($"{currency}_24h_change", out var ch)
            ? ch
            : (decimal?)null;
        return new CryptoPrice(coin, currency, price, change);
    }

    private async Task<IReadOnlyList<TrendingCoin>> FetchTrendingAsync(CancellationToken ct)
    {
        var dto = await GetAsync<TrendingResponseDto>("api/v3/search/trending", ct)
            .ConfigureAwait(false);
        if (dto?.Coins is null)
        {
            return [];
        }

        return dto.Coins
            .Where(c => c.Item is not null)
            .Select(c => new TrendingCoin(
                c.Item!.Id ?? string.Empty,
                c.Item.Name ?? string.Empty,
                (c.Item.Symbol ?? string.Empty).ToUpperInvariant(),
                c.Item.MarketCapRank))
            .ToList();
    }

    /// <summary>
    /// Serve <paramref name="key"/> from the short-TTL cache when fresh, otherwise
    /// throttle, fetch, and cache the result (including negatives).
    /// </summary>
    private async Task<T?> GetOrFetchAsync<T>(
        string key, Func<CancellationToken, Task<T?>> fetch, CancellationToken ct)
        where T : class
    {
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > _clock.GetUtcNow())
        {
            return (T?)entry.Value;
        }

        await ThrottleAsync(ct).ConfigureAwait(false);
        var value = await fetch(ct).ConfigureAwait(false);
        _cache[key] = new CacheEntry(_clock.GetUtcNow() + CacheTtl, value);
        return value;
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = _clock.GetUtcNow() - _lastRequestUtc;
            if (elapsed < MinRequestInterval)
            {
                await Task.Delay(MinRequestInterval - elapsed, _clock, ct).ConfigureAwait(false);
            }

            _lastRequestUtc = _clock.GetUtcNow();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// GET that maps non-success responses and unreadable bodies to
    /// <see cref="MarketsApiException"/>. Transport faults and timeouts bubble
    /// unchanged so the plugin can distinguish them.
    /// </summary>
    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
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
            throw new MarketsApiException(
                "The crypto price service returned an unreadable response. Try again later.");
        }
    }

    private static MarketsApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests)
        {
            return new MarketsApiException(
                "The crypto price service is rate limiting requests. Try again shortly.", status);
        }

        if ((int)status >= 500)
        {
            return new MarketsApiException(
                "The crypto price service is currently unavailable. Try again later.", status);
        }

        return new MarketsApiException(
            $"The crypto price request failed ({(int)status}).", status);
    }

    private sealed record CacheEntry(DateTimeOffset ExpiresAt, object? Value);

    private sealed record TrendingResponseDto(
        [property: JsonPropertyName("coins")] List<TrendingItemDto>? Coins);

    private sealed record TrendingItemDto(
        [property: JsonPropertyName("item")] TrendingCoinDto? Item);

    private sealed record TrendingCoinDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("symbol")] string? Symbol,
        [property: JsonPropertyName("market_cap_rank")] int? MarketCapRank);
}
