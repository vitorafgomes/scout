using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Scout.Plugin.Wikipedia.Abstractions;

namespace Scout.Plugin.Wikipedia;

/// <summary>
/// Thin read-only Wikimedia REST client over an injected, pre-configured
/// <see cref="HttpClient"/> (base address, timeout, and User-Agent are set by DI).
/// All endpoints are GETs. Non-success responses become a user-safe
/// <see cref="WikipediaApiException"/>; timeouts and transport faults bubble
/// unchanged. Article text is untrusted external content: markup and entities are
/// stripped so values are relayed strictly as data.
/// </summary>
internal sealed class WikipediaClient(HttpClient http) : IWikipediaClient
{
    private const int MaxSearchResults = 5;
    private const int MaxOnThisDayEvents = 5;
    private const int MaxExtractLength = 1500;

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<WikipediaSearchResult>> SearchAsync(
        string query, string language, CancellationToken ct)
    {
        var url =
            $"https://{language}.wikipedia.org/w/api.php?action=query&list=search" +
            $"&srsearch={Uri.EscapeDataString(query)}&format=json&srlimit={MaxSearchResults}";

        var response = await GetAsync<SearchResponseDto>(url, ct).ConfigureAwait(false);
        var items = response.Query?.Search ?? [];

        return items
            .Where(i => !string.IsNullOrWhiteSpace(i.Title))
            .Select(i => new WikipediaSearchResult(i.Title!, CleanText(i.Snippet)))
            .ToList();
    }

    public async Task<WikipediaArticleSummary?> GetSummaryAsync(
        string title, string language, CancellationToken ct)
    {
        var url =
            $"https://{language}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title)}";

        // A 404 means the article does not exist and yields null; other
        // non-success statuses surface as WikipediaApiException.
        var dto = await TryGetAsync<SummaryDto>(url, ct).ConfigureAwait(false);
        if (dto is null)
        {
            return null;
        }

        return new WikipediaArticleSummary(
            Title: NullIfBlank(dto.Title) ?? title,
            Extract: CapExtract(dto.Extract),
            Url: dto.ContentUrls?.Desktop?.Page ?? string.Empty);
    }

    public async Task<IReadOnlyList<WikipediaOnThisDayEvent>> GetOnThisDayAsync(CancellationToken ct)
    {
        // A fixed illustrative endpoint keyed to today's date (UTC). The month and
        // day are zero-padded to two digits as the feed requires.
        var now = DateTime.UtcNow;
        var url =
            $"https://api.wikimedia.org/feed/v1/wikipedia/en/onthisday/events/{now:MM}/{now:dd}";

        var dto = await GetAsync<OnThisDayResponseDto>(url, ct).ConfigureAwait(false);
        var events = dto.Events ?? [];

        return events
            .Where(e => !string.IsNullOrWhiteSpace(e.Text))
            .Take(MaxOnThisDayEvents)
            .Select(e => new WikipediaOnThisDayEvent(CleanText(e.Text), e.Year))
            .ToList();
    }

    /// <summary>
    /// GET mapping non-success responses to <see cref="WikipediaApiException"/>.
    /// Transport faults and timeouts bubble unchanged.
    /// </summary>
    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        var value = await response.Content
            .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        return value ?? throw new WikipediaApiException(
            "Wikipedia returned an unexpected empty response.");
    }

    /// <summary>
    /// GET that returns null on 404 (resource absent) and maps other non-success
    /// responses to <see cref="WikipediaApiException"/>. Transport faults and
    /// timeouts bubble unchanged.
    /// </summary>
    private async Task<T?> TryGetAsync<T>(string url, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        return await response.Content
            .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
    }

    private static WikipediaApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.NotFound:
                return new WikipediaApiException(
                    "Wikipedia article not found.", status);
            case HttpStatusCode.TooManyRequests:
                return new WikipediaApiException(
                    "Wikipedia rate limit reached. Try again later.", status);
            default:
                if ((int)status >= 500)
                {
                    return new WikipediaApiException(
                        "Wikipedia is currently unavailable. Try again later.", status);
                }

                return new WikipediaApiException(
                    $"Wikipedia request failed ({(int)status}).", status);
        }
    }

    private static string CapExtract(string? extract)
    {
        if (string.IsNullOrWhiteSpace(extract))
        {
            return string.Empty;
        }

        var text = extract.Trim();
        return text.Length <= MaxExtractLength ? text : text[..MaxExtractLength];
    }

    /// <summary>
    /// Strip HTML tags and decode entities, collapsing whitespace to a single
    /// line. The search snippet and event text are untrusted external content and
    /// are surfaced only as inert data.
    /// </summary>
    private static string CleanText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        var insideTag = false;
        foreach (var ch in raw)
        {
            if (ch == '<')
            {
                insideTag = true;
                continue;
            }

            if (ch == '>')
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
            {
                builder.Append(ch);
            }
        }

        var decoded = WebUtility.HtmlDecode(builder.ToString());
        return string.Join(' ', decoded.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record SearchResponseDto(
        [property: JsonPropertyName("query")] SearchQueryDto? Query);

    private sealed record SearchQueryDto(
        [property: JsonPropertyName("search")] List<SearchItemDto>? Search);

    private sealed record SearchItemDto(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("snippet")] string? Snippet);

    private sealed record SummaryDto(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("extract")] string? Extract,
        [property: JsonPropertyName("content_urls")] ContentUrlsDto? ContentUrls);

    private sealed record ContentUrlsDto(
        [property: JsonPropertyName("desktop")] DesktopUrlsDto? Desktop);

    private sealed record DesktopUrlsDto(
        [property: JsonPropertyName("page")] string? Page);

    private sealed record OnThisDayResponseDto(
        [property: JsonPropertyName("events")] List<EventItemDto>? Events);

    private sealed record EventItemDto(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("year")] int? Year);
}
