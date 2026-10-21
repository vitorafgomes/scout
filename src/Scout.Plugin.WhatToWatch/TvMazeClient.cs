using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.WhatToWatch.Abstractions;

namespace Scout.Plugin.WhatToWatch;

/// <summary>
/// Thin read-only client over the free TVMaze REST API using an injected,
/// pre-configured <see cref="HttpClient"/> (base address, timeout, and User-Agent
/// are set by DI). Non-success responses and unreadable bodies become a user-safe
/// <see cref="WhatToWatchApiException"/>; timeouts and transport faults bubble
/// unchanged. Every call is a GET.
/// </summary>
internal sealed class TvMazeClient(HttpClient http) : ITvMazeClient
{
    private const string WireDateFormat = "yyyy-MM-dd";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<Show>> SearchShowsAsync(string title, CancellationToken ct)
    {
        var items = await GetAsync<List<SearchResultDto>>(
            $"search/shows?q={Escape(title)}", ct).ConfigureAwait(false);
        return items
            .Where(i => i.Show is not null)
            .Select(i => MapShow(i.Show!))
            .ToList();
    }

    public async Task<ShowEpisode?> GetNextEpisodeAsync(string title, CancellationToken ct)
    {
        // singlesearch returns 404 (no body) when nothing matches; TryGetAsync
        // maps that to null so the plugin can distinguish "unknown show".
        var dto = await TryGetAsync<SingleShowDto>(
            $"singlesearch/shows?q={Escape(title)}&embed=nextepisode", ct).ConfigureAwait(false);
        if (dto is null)
        {
            return null;
        }

        return new ShowEpisode(
            string.IsNullOrWhiteSpace(dto.Name) ? title : dto.Name,
            MapEpisode(dto.Embedded?.NextEpisode));
    }

    public async Task<IReadOnlyList<Show>> RecommendShowsAsync(string genre, CancellationToken ct)
    {
        // TVMaze has no genre-filter endpoint; page 0 is a fixed, well-rated
        // catalogue slice we filter and rank client-side.
        var items = await GetAsync<List<ShowDto>>("shows?page=0", ct).ConfigureAwait(false);
        return items
            .Where(s => s.Genres is not null
                && s.Genres.Any(g => g.Equals(genre, StringComparison.OrdinalIgnoreCase)))
            .Select(MapShow)
            .OrderByDescending(s => s.Rating ?? -1d)
            .ToList();
    }

    private static Show MapShow(ShowDto dto) => new(
        string.IsNullOrWhiteSpace(dto.Name) ? "Unknown" : dto.Name,
        ParseYear(dto.Premiered),
        NetworkName(dto),
        string.IsNullOrWhiteSpace(dto.Status) ? "Unknown" : dto.Status,
        dto.Genres ?? [],
        dto.Rating?.Average);

    private static Episode? MapEpisode(EpisodeDto? dto)
        => dto is null
            ? null
            : new Episode(
                dto.Season,
                dto.Number,
                string.IsNullOrWhiteSpace(dto.Name) ? "TBA" : dto.Name,
                ParseDate(dto.Airdate));

    private static string? NetworkName(ShowDto dto)
    {
        var name = dto.Network?.Name ?? dto.WebChannel?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static int? ParseYear(string? premiered)
        => DateOnly.TryParseExact(
                premiered, WireDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Year
            : null;

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParseExact(
                value, WireDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    /// <summary>
    /// GET that returns null on 404 (resource absent) and maps other non-success
    /// responses and unreadable bodies to <see cref="WhatToWatchApiException"/>.
    /// Timeouts and transport faults bubble unchanged.
    /// </summary>
    private async Task<T?> TryGetAsync<T>(string path, CancellationToken ct)
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

        return await ReadJsonAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        return await ReadJsonAsync<T>(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
        where T : class
    {
        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new WhatToWatchApiException(
                "The show database returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new WhatToWatchApiException(
                "The show database returned an unreadable response.");
        }
    }

    private static WhatToWatchApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.TooManyRequests:
                return new WhatToWatchApiException(
                    "The show database is rate limiting requests. Try again later.", status);
            default:
                if ((int)status >= 500)
                {
                    return new WhatToWatchApiException(
                        "The show database is currently unavailable. Try again later.", status);
                }

                return new WhatToWatchApiException(
                    $"The show database request failed ({(int)status}).", status);
        }
    }

    private sealed record SearchResultDto(
        [property: JsonPropertyName("show")] ShowDto? Show);

    private sealed record ShowDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("premiered")] string? Premiered,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("genres")] List<string>? Genres,
        [property: JsonPropertyName("rating")] RatingDto? Rating,
        [property: JsonPropertyName("network")] NetworkDto? Network,
        [property: JsonPropertyName("webChannel")] NetworkDto? WebChannel);

    private sealed record RatingDto(
        [property: JsonPropertyName("average")] double? Average);

    private sealed record NetworkDto(
        [property: JsonPropertyName("name")] string? Name);

    private sealed record SingleShowDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("_embedded")] EmbeddedDto? Embedded);

    private sealed record EmbeddedDto(
        [property: JsonPropertyName("nextepisode")] EpisodeDto? NextEpisode);

    private sealed record EpisodeDto(
        [property: JsonPropertyName("season")] int Season,
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("airdate")] string? Airdate);
}
