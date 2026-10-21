using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.WhatToWatch.Abstractions;

namespace Scout.Plugin.WhatToWatch;

/// <summary>
/// Thin read-only client over the TMDB REST API using an injected, pre-configured
/// <see cref="HttpClient"/> (base address, timeout, and User-Agent are set by DI)
/// plus a v3 API key appended to each request. Resolves a title to streaming
/// availability in a country. Non-success responses and unreadable bodies become a
/// user-safe <see cref="WhatToWatchApiException"/>; timeouts and transport faults
/// bubble unchanged. Every call is a GET.
/// </summary>
internal sealed class TmdbClient : ITmdbClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    /// <summary>Creates the client over a configured HttpClient and a TMDB v3 key.</summary>
    /// <param name="http">Pre-configured HttpClient. Must be non-null.</param>
    /// <param name="apiKey">TMDB v3 API key. Must be non-empty.</param>
    public TmdbClient(HttpClient http, string apiKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = string.IsNullOrWhiteSpace(apiKey)
            ? throw new ArgumentException("A TMDB API key is required.", nameof(apiKey))
            : apiKey;
    }

    public async Task<WatchAvailability?> GetWatchProvidersAsync(
        string title, string country, CancellationToken ct)
    {
        var search = await GetAsync<SearchResponseDto>(
            $"3/search/tv?query={Escape(title)}&{KeyQuery()}", ct).ConfigureAwait(false);
        var match = search.Results?.FirstOrDefault();
        if (match is null)
        {
            return null;
        }

        var providers = await GetAsync<ProvidersResponseDto>(
            $"3/tv/{match.Id}/watch/providers?{KeyQuery()}", ct).ConfigureAwait(false);

        CountryProvidersDto? forCountry = null;
        providers.Results?.TryGetValue(country, out forCountry);

        return new WatchAvailability(
            string.IsNullOrWhiteSpace(match.Name) ? title : match.Name,
            country,
            Names(forCountry?.Flatrate),
            Names(forCountry?.Rent),
            Names(forCountry?.Buy));
    }

    private static IReadOnlyList<string> Names(List<ProviderDto>? providers)
        => providers is null
            ? []
            : providers
                .Where(p => !string.IsNullOrWhiteSpace(p.ProviderName))
                .Select(p => p.ProviderName!)
                .ToList();

    private string KeyQuery() => $"api_key={Escape(_apiKey)}";

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new WhatToWatchApiException(
                "TMDB returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new WhatToWatchApiException(
                "TMDB returned an unreadable response.");
        }
    }

    private static WhatToWatchApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                return new WhatToWatchApiException(
                    "TMDB authentication failed. Check the configured TMDB key.", status);
            case HttpStatusCode.TooManyRequests:
                return new WhatToWatchApiException(
                    "TMDB is rate limiting requests. Try again later.", status);
            default:
                if ((int)status >= 500)
                {
                    return new WhatToWatchApiException(
                        "TMDB is currently unavailable. Try again later.", status);
                }

                return new WhatToWatchApiException(
                    $"The TMDB request failed ({(int)status}).", status);
        }
    }

    private sealed record SearchResponseDto(
        [property: JsonPropertyName("results")] List<SearchMatchDto>? Results);

    private sealed record SearchMatchDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record ProvidersResponseDto(
        [property: JsonPropertyName("results")] Dictionary<string, CountryProvidersDto>? Results);

    private sealed record CountryProvidersDto(
        [property: JsonPropertyName("flatrate")] List<ProviderDto>? Flatrate,
        [property: JsonPropertyName("rent")] List<ProviderDto>? Rent,
        [property: JsonPropertyName("buy")] List<ProviderDto>? Buy);

    private sealed record ProviderDto(
        [property: JsonPropertyName("provider_name")] string? ProviderName);
}
