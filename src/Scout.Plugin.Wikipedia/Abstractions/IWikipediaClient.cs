namespace Scout.Plugin.Wikipedia.Abstractions;

/// <summary>
/// Read-only access to the Wikimedia REST API. Retrieval only: every call is a
/// GET and no operation mutates remote state by design.
/// </summary>
public interface IWikipediaClient
{
    /// <summary>
    /// Search articles on the <paramref name="language"/> Wikipedia for
    /// <paramref name="query"/>. Returns up to five hits, each with a plain-text
    /// snippet.
    /// </summary>
    Task<IReadOnlyList<WikipediaSearchResult>> SearchAsync(
        string query, string language, CancellationToken ct);

    /// <summary>
    /// Fetch the introductory summary of <paramref name="title"/> on the
    /// <paramref name="language"/> Wikipedia. Returns null when the article does
    /// not exist (404).
    /// </summary>
    Task<WikipediaArticleSummary?> GetSummaryAsync(
        string title, string language, CancellationToken ct);

    /// <summary>
    /// Fetch notable historical events for today's date from Wikipedia's
    /// "On this day" feed. Returns up to five events.
    /// </summary>
    Task<IReadOnlyList<WikipediaOnThisDayEvent>> GetOnThisDayAsync(CancellationToken ct);
}
