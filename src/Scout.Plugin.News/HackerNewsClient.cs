using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.News.Abstractions;

namespace Scout.Plugin.News;

/// <summary>
/// Thin read-only client over the public Hacker News Algolia API using an injected,
/// pre-configured <see cref="HttpClient"/> (base address, timeout, and User-Agent
/// are set by DI). The API needs no key. Non-success responses and unreadable
/// bodies become a user-safe <see cref="NewsApiException"/>; timeouts and transport
/// faults bubble unchanged. Every call is a GET.
/// </summary>
internal sealed class HackerNewsClient(HttpClient http) : IHackerNewsClient
{
    private const int HitsPerPage = 20;
    private const string ItemBaseUrl = "https://news.ycombinator.com/item?id=";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<HackerNewsStory>> GetTopStoriesAsync(CancellationToken ct)
    {
        var response = await GetAsync<SearchResponseDto>(
            $"api/v1/search?tags=front_page&hitsPerPage={HitsPerPage}", ct).ConfigureAwait(false);
        return Map(response);
    }

    public async Task<IReadOnlyList<HackerNewsStory>> SearchAsync(string query, CancellationToken ct)
    {
        var response = await GetAsync<SearchResponseDto>(
            $"api/v1/search?query={Uri.EscapeDataString(query)}&tags=story&hitsPerPage={HitsPerPage}", ct)
            .ConfigureAwait(false);
        return Map(response);
    }

    private static IReadOnlyList<HackerNewsStory> Map(SearchResponseDto response)
    {
        var hits = response.Hits ?? [];
        return hits
            .Where(h => !string.IsNullOrWhiteSpace(h.Title))
            .Select(h => new HackerNewsStory(
                h.Title!.Trim(),
                string.IsNullOrWhiteSpace(h.Url) ? ItemUrl(h.ObjectId) : h.Url!.Trim(),
                h.Points ?? 0))
            .ToList();
    }

    private static string ItemUrl(string? objectId)
        => string.IsNullOrWhiteSpace(objectId)
            ? "https://news.ycombinator.com/"
            : ItemBaseUrl + objectId;

    /// <summary>
    /// GET that maps non-success responses and unreadable bodies to
    /// <see cref="NewsApiException"/>. Timeouts and transport faults bubble
    /// unchanged so the plugin can distinguish them.
    /// </summary>
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
            return value ?? throw new NewsApiException(
                "Hacker News returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new NewsApiException("Hacker News returned an unreadable response.");
        }
    }

    private static NewsApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.TooManyRequests:
                return new NewsApiException(
                    "Hacker News is rate limiting requests. Try again later.", status);
            default:
                if ((int)status >= 500)
                {
                    return new NewsApiException(
                        "Hacker News is currently unavailable. Try again later.", status);
                }

                return new NewsApiException(
                    $"The Hacker News request failed ({(int)status}).", status);
        }
    }

    private sealed record SearchResponseDto(
        [property: JsonPropertyName("hits")] List<HitDto>? Hits);

    private sealed record HitDto(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("points")] int? Points,
        [property: JsonPropertyName("objectID")] string? ObjectId);
}
