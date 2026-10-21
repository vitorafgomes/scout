using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Scout.Plugin.WebSearch.Abstractions;

namespace Scout.Plugin.WebSearch;

/// <summary>
/// Thin read-only client over the web sources, using an injected, pre-configured
/// <see cref="HttpClient"/> (timeout and User-Agent are set by DI). Full-text
/// search goes to the configured SearXNG instance; instant answers go to the
/// DuckDuckGo Instant Answer API; page fetch reads any absolute URL. Non-success
/// responses and unreadable bodies become a user-safe
/// <see cref="WebSearchApiException"/>; timeouts and transport faults bubble
/// unchanged. The page body is read with a hard byte budget so a hostile or
/// oversized URL cannot exhaust memory. Every call is a GET.
/// </summary>
internal sealed class WebSearchClient : IWebSearchClient
{
    private const string DuckDuckGoUrl = "https://api.duckduckgo.com/";

    // Upper bound on the characters read from a fetched page before stripping. The
    // model-visible text is capped far lower by the plugin; this cap only stops an
    // oversized or slow-draining body from exhausting memory.
    private const int MaxDownloadChars = 500_000;

    private readonly HttpClient _http;
    private readonly string _searxUrl;

    public WebSearchClient(HttpClient http, IOptions<WebSearchOptions> options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(options);
        var raw = options.Value.SearxUrl?.Trim() ?? string.Empty;
        _searxUrl = raw.TrimEnd('/');
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_searxUrl))
        {
            throw new WebSearchApiException("Web search is not configured.");
        }

        var uri = $"{_searxUrl}/search?q={Uri.EscapeDataString(query)}&format=json&safesearch=1";
        var dto = await GetJsonAsync<SearxResponse>(uri, "web search service", ct).ConfigureAwait(false);
        var results = dto.Results ?? new List<SearxResult>();
        return results
            .Select(r => new WebSearchResult(r.Title ?? string.Empty, r.Url ?? string.Empty, r.Content ?? string.Empty))
            .ToList();
    }

    public async Task<InstantAnswer> GetInstantAnswerAsync(string query, CancellationToken ct)
    {
        var uri = $"{DuckDuckGoUrl}?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
        var dto = await GetJsonAsync<DuckDuckGoResponse>(uri, "instant answer service", ct).ConfigureAwait(false);
        return MapAnswer(dto);
    }

    public async Task<FetchedPage> FetchPageAsync(string url, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response, "page");
        }

        var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var isHtml = media.Length == 0
            || media.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
        var isText = media.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
        if (!isHtml && !isText)
        {
            // Not readable text (image, PDF, binary): report the type without
            // downloading the body.
            return new FetchedPage(false, false, media, string.Empty);
        }

        var body = await ReadBoundedAsync(response.Content, ct).ConfigureAwait(false);
        var reportedMedia = media.Length == 0 ? "text/html" : media;
        return new FetchedPage(true, isHtml, reportedMedia, body);
    }

    private static InstantAnswer MapAnswer(DuckDuckGoResponse dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.Answer))
        {
            return new InstantAnswer(dto.Answer.Trim(), dto.AbstractUrl ?? string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(dto.AbstractText))
        {
            return new InstantAnswer(dto.AbstractText.Trim(), dto.AbstractUrl ?? string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(dto.Definition))
        {
            return new InstantAnswer(dto.Definition.Trim(), dto.DefinitionUrl ?? string.Empty);
        }

        var topic = dto.RelatedTopics?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));
        if (topic is not null)
        {
            return new InstantAnswer(topic.Text!.Trim(), topic.FirstUrl ?? string.Empty);
        }

        return new InstantAnswer(string.Empty, string.Empty);
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var builder = new StringBuilder();
        int read;
        while (builder.Length < MaxDownloadChars
            && (read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            var take = Math.Min(read, MaxDownloadChars - builder.Length);
            builder.Append(buffer, 0, take);
        }

        return builder.ToString();
    }

    /// <summary>
    /// GET that maps non-success responses and unreadable bodies to
    /// <see cref="WebSearchApiException"/>. Timeouts and transport faults bubble
    /// unchanged so the plugin can distinguish them.
    /// </summary>
    private async Task<T> GetJsonAsync<T>(string uri, string subject, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response, subject);
        }

        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new WebSearchApiException(
                $"The {subject} returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new WebSearchApiException($"The {subject} returned an unreadable response.");
        }
    }

    private static WebSearchApiException ToApiException(HttpResponseMessage response, string subject)
    {
        var status = response.StatusCode;
        return status switch
        {
            HttpStatusCode.NotFound => new WebSearchApiException(
                $"The {subject} could not be found (404).", status),
            HttpStatusCode.TooManyRequests => new WebSearchApiException(
                $"The {subject} is rate limiting requests. Try again later.", status),
            _ when (int)status >= 500 => new WebSearchApiException(
                $"The {subject} is currently unavailable. Try again later.", status),
            _ => new WebSearchApiException(
                $"The {subject} request failed ({(int)status}).", status),
        };
    }

    private sealed record SearxResponse(
        [property: JsonPropertyName("results")] List<SearxResult>? Results);

    private sealed record SearxResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("content")] string? Content);

    private sealed record DuckDuckGoResponse(
        [property: JsonPropertyName("Answer")] string? Answer,
        [property: JsonPropertyName("AbstractText")] string? AbstractText,
        [property: JsonPropertyName("AbstractURL")] string? AbstractUrl,
        [property: JsonPropertyName("Definition")] string? Definition,
        [property: JsonPropertyName("DefinitionURL")] string? DefinitionUrl,
        [property: JsonPropertyName("Heading")] string? Heading,
        [property: JsonPropertyName("RelatedTopics")] List<DuckDuckGoTopic>? RelatedTopics);

    private sealed record DuckDuckGoTopic(
        [property: JsonPropertyName("Text")] string? Text,
        [property: JsonPropertyName("FirstURL")] string? FirstUrl);
}
