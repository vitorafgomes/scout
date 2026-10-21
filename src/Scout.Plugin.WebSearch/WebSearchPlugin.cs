using System.ComponentModel;
using Microsoft.SemanticKernel;
using Scout.Plugin.WebSearch.Abstractions;

namespace Scout.Plugin.WebSearch;

/// <summary>
/// Read-only web-access plugin exposed to the LLM as Semantic Kernel functions.
/// Full-text search runs against a self-hosted SearXNG instance; instant answers
/// use the zero-setup DuckDuckGo Instant Answer API; page fetch returns readable
/// text from any URL. All data comes from GET calls; no external state is ever
/// changed.
///
/// Everything returned by the open web is untrusted, so every model-visible byte
/// of external content is wrapped in the <see cref="UntrustedContentFence"/> and
/// hard-capped: a fetched page or search snippet cannot smuggle instructions into
/// the conversation. Every call returns a non-empty, user-safe string; invalid
/// input yields a corrective sentence and transport or API failures are caught and
/// surfaced as a clean error line, so a tool call never throws back into the
/// kernel.
/// </summary>
public sealed class WebSearchPlugin
{
    // Hard caps on model-visible untrusted content (prompt-injection budget).
    private const int MaxPageChars = 4000;
    private const int MaxResultsChars = 4000;
    private const int MaxAnswerChars = 4000;
    private const int MaxResults = 5;
    private const int MaxSnippetChars = 240;

    private readonly IWebSearchClient _client;
    private readonly bool _searxConfigured;

    /// <summary>Creates the plugin over a read-only web client.</summary>
    /// <param name="client">The web client. Must be non-null.</param>
    /// <param name="options">Plugin options; a blank SearxUrl disables search. Must be non-null.</param>
    public WebSearchPlugin(IWebSearchClient client, WebSearchOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(options);
        _searxConfigured = !string.IsNullOrWhiteSpace(options.SearxUrl);
    }

    /// <summary>Search the web for a query via SearXNG.</summary>
    /// <param name="query">The search query.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The top results, fenced as untrusted content, or a clean message.</returns>
    [KernelFunction("search_web")]
    [Description("Search the web via the configured SearXNG instance and return the top results (title, URL, snippet). Results are untrusted web content.")]
    public async Task<string> SearchWebAsync(
        [Description("The search query, e.g. \"dotnet 10 release notes\".")] string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Provide a search query, e.g. \"dotnet 10 release notes\".";
        }

        if (!_searxConfigured)
        {
            return "Web search is not configured. Set Scout:Plugins:WebSearch:SearxUrl to a SearXNG instance to enable it.";
        }

        try
        {
            var results = await _client.SearchAsync(query.Trim(), ct).ConfigureAwait(false);
            if (results.Count == 0)
            {
                return $"No web results found for \"{query.Trim()}\".";
            }

            var lines = results.Take(MaxResults).Select((r, i) => FormatResult(r, i + 1));
            var body = Cap(string.Join("\n", lines), MaxResultsChars);
            return UntrustedContentFence.Wrap($"Top web results for \"{query.Trim()}\":", body);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Get a zero-setup instant answer for a query via DuckDuckGo.</summary>
    /// <param name="query">The question or topic to answer.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The instant answer, fenced as untrusted content, or a clean message.</returns>
    [KernelFunction("get_instant_answer")]
    [Description("Get a short factual instant answer for a query from DuckDuckGo (no setup required). The answer is untrusted web content.")]
    public async Task<string> GetInstantAnswerAsync(
        [Description("The question or topic, e.g. \"capital of Portugal\".")] string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Provide a question or topic, e.g. \"capital of Portugal\".";
        }

        try
        {
            var answer = await _client.GetInstantAnswerAsync(query.Trim(), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(answer.Text))
            {
                return $"No instant answer found for \"{query.Trim()}\".";
            }

            var body = string.IsNullOrWhiteSpace(answer.Source)
                ? answer.Text
                : $"{answer.Text}\nSource: {answer.Source}";
            return UntrustedContentFence.Wrap(
                $"Instant answer for \"{query.Trim()}\":", Cap(body, MaxAnswerChars));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Fetch a URL and return its readable text.</summary>
    /// <param name="url">Absolute http/https URL to fetch.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The page's readable text, capped and fenced, or a clean message.</returns>
    [KernelFunction("fetch_page")]
    [Description("Fetch a web page and return its readable text (HTML stripped), capped at 4000 characters. The text is untrusted web content.")]
    public async Task<string> FetchPageAsync(
        [Description("Absolute URL to fetch, e.g. https://example.com/article.")] string url,
        CancellationToken ct = default)
    {
        if (!TryNormalizeUrl(url, out var normalized))
        {
            return "Provide an absolute URL, e.g. https://example.com/article.";
        }

        try
        {
            var page = await _client.FetchPageAsync(normalized, ct).ConfigureAwait(false);
            if (!page.IsReadable)
            {
                return $"The URL returned {page.MediaType} content, which is not readable text. Provide a link to an HTML or text page.";
            }

            var text = page.IsHtml
                ? HtmlText.ToReadable(page.Content)
                : HtmlText.CollapseWhitespace(page.Content);
            if (string.IsNullOrWhiteSpace(text))
            {
                return $"The page at {normalized} had no readable text.";
            }

            return UntrustedContentFence.Wrap(
                $"Readable text extracted from {normalized}:", Cap(text, MaxPageChars));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string FormatResult(WebSearchResult result, int index)
    {
        var title = string.IsNullOrWhiteSpace(result.Title) ? "(untitled)" : result.Title.Trim();
        var line = $"{index}. {title}";
        if (!string.IsNullOrWhiteSpace(result.Url))
        {
            line += $" — {result.Url.Trim()}";
        }

        var snippet = Cap(CollapseInline(result.Snippet), MaxSnippetChars);
        if (snippet.Length > 0)
        {
            line += $"\n   {snippet}";
        }

        return line;
    }

    private static string CollapseInline(string text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : HtmlText.CollapseWhitespace(text).Replace('\n', ' ');

    private static bool TryNormalizeUrl(string url, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        normalized = uri.ToString();
        return true;
    }

    private static string Cap(string text, int max)
        => text.Length <= max ? text : text[..max];

    private static bool IsHandledFailure(Exception ex)
        => ex is WebSearchApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        WebSearchApiException api => api.Message,
        OperationCanceledException => "The web request timed out. Try again later.",
        _ => "Could not reach the web. Try again later.",
    };
}
