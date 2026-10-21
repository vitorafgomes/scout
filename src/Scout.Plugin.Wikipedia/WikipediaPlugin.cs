using System.ComponentModel;
using Microsoft.SemanticKernel;
using Scout.Plugin.Wikipedia.Abstractions;

namespace Scout.Plugin.Wikipedia;

/// <summary>
/// Read-only Wikipedia plugin exposed to the LLM as Semantic Kernel functions for
/// encyclopedic grounding. Each method is a <see cref="KernelFunctionAttribute"/>
/// the model can invoke by name through function-calling. Retrieval only: every
/// call is a GET against the Wikimedia API, nothing is mutated. Every call returns
/// a non-empty, user-safe string; transport and API failures are caught and
/// surfaced as a clean error line so a tool call never throws back into the
/// kernel. Article text is untrusted external content and is relayed as data.
/// </summary>
public sealed class WikipediaPlugin
{
    private const string DefaultLanguage = "en";

    private readonly IWikipediaClient _client;

    /// <summary>Creates the plugin over a read-only Wikipedia client.</summary>
    /// <param name="client">The Wikipedia client. Must be non-null.</param>
    public WikipediaPlugin(IWikipediaClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Search Wikipedia and return the top matching article titles with snippets.</summary>
    /// <param name="query">Free-text search term.</param>
    /// <param name="language">Wikipedia language code, e.g. "pt".</param>
    /// <param name="ct">Cancels the underlying Wikipedia request.</param>
    /// <returns>Up to five results, one per line, or a clean message when none or on failure.</returns>
    [KernelFunction("search_wikipedia")]
    [Description("Search Wikipedia for a topic and return the top five matching article titles, each with a short snippet. Use this to find the right article before fetching its summary.")]
    public async Task<string> SearchWikipediaAsync(
        [Description("Free-text search term, e.g. Alan Turing.")] string query,
        [Description("Wikipedia language code, e.g. en for English or pt for Portuguese.")] string language = DefaultLanguage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Provide a search term, for example: search_wikipedia with query \"Alan Turing\".";
        }

        var lang = NormalizeLanguage(language);
        if (!IsValidLanguageCode(lang))
        {
            return LanguageHint(language);
        }

        try
        {
            var results = await _client.SearchAsync(query, lang, ct).ConfigureAwait(false);
            return results.Count == 0
                ? $"No Wikipedia articles found for {query}."
                : string.Join("\n", results.Select(FormatSearchResult));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Get the intro summary of a named Wikipedia article plus its canonical URL.</summary>
    /// <param name="title">Exact article title.</param>
    /// <param name="language">Wikipedia language code, e.g. "pt".</param>
    /// <param name="ct">Cancels the underlying Wikipedia request.</param>
    /// <returns>The capped summary and URL, a not-found sentence, or a clean error on failure.</returns>
    [KernelFunction("get_article_summary")]
    [Description("Get the introductory summary of a specific Wikipedia article by exact title, capped in length, plus the canonical article URL. Use search_wikipedia first if unsure of the exact title.")]
    public async Task<string> GetArticleSummaryAsync(
        [Description("Exact article title, e.g. Alan Turing.")] string title,
        [Description("Wikipedia language code, e.g. en for English or pt for Portuguese.")] string language = DefaultLanguage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Provide an article title, for example: get_article_summary with title \"Alan Turing\".";
        }

        var lang = NormalizeLanguage(language);
        if (!IsValidLanguageCode(lang))
        {
            return LanguageHint(language);
        }

        try
        {
            var summary = await _client.GetSummaryAsync(title, lang, ct).ConfigureAwait(false);
            if (summary is null)
            {
                return $"No Wikipedia article named {title}.";
            }

            var extract = string.IsNullOrWhiteSpace(summary.Extract)
                ? "(no summary text available)"
                : summary.Extract;
            var url = string.IsNullOrWhiteSpace(summary.Url)
                ? "(URL unavailable)"
                : summary.Url;

            return $"{extract}\nURL: {url}";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List a few notable historical events for today's date.</summary>
    /// <param name="ct">Cancels the underlying Wikipedia request.</param>
    /// <returns>Three to five events, one per line, or a friendly line when unavailable.</returns>
    [KernelFunction("get_on_this_day")]
    [Description("List notable historical events that happened on today's date, sourced from Wikipedia's On This Day feed.")]
    public async Task<string> GetOnThisDayAsync(CancellationToken ct = default)
    {
        try
        {
            var events = await _client.GetOnThisDayAsync(ct).ConfigureAwait(false);
            return events.Count == 0
                ? "No notable historical events found for today. Try again later."
                : string.Join("\n", events.Select(FormatEvent));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string FormatSearchResult(WikipediaSearchResult result)
        => string.IsNullOrWhiteSpace(result.Snippet)
            ? result.Title
            : $"{result.Title} - {result.Snippet}";

    private static string FormatEvent(WikipediaOnThisDayEvent e)
        => e.Year is int year ? $"{year}: {e.Text}" : e.Text;

    private static string NormalizeLanguage(string language)
        => string.IsNullOrWhiteSpace(language)
            ? DefaultLanguage
            : language.Trim().ToLowerInvariant();

    private static bool IsValidLanguageCode(string language)
    {
        if (language.Length is < 2 or > 20)
        {
            return false;
        }

        foreach (var ch in language)
        {
            if (!char.IsAsciiLetterLower(ch) && ch != '-')
            {
                return false;
            }
        }

        return char.IsAsciiLetterLower(language[0]);
    }

    private static string LanguageHint(string language)
        => $"'{language}' is not a valid Wikipedia language code. Use a code like \"en\", \"pt\", or \"es\".";

    private static bool IsHandledFailure(Exception ex)
        => ex is WikipediaApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        WikipediaApiException api => api.Message,
        OperationCanceledException => "Wikipedia request timed out. Try again later.",
        _ => "Could not reach Wikipedia. Try again later.",
    };
}
