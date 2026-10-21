namespace Scout.Plugin.WebSearch.Abstractions;

/// <summary>
/// Read-only access to the web: full-text search via a self-hosted SearXNG
/// instance, zero-setup instant answers via the DuckDuckGo Instant Answer API,
/// and single-page fetch. Every operation is a GET; no external state is ever
/// mutated. All returned text is untrusted and must be fenced by the caller
/// before it reaches the model.
/// </summary>
public interface IWebSearchClient
{
    /// <summary>
    /// Search the configured SearXNG instance for <paramref name="query"/>,
    /// most-relevant first.
    /// </summary>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken ct);

    /// <summary>
    /// The DuckDuckGo instant answer for <paramref name="query"/>, or an empty
    /// answer when the service has none.
    /// </summary>
    Task<InstantAnswer> GetInstantAnswerAsync(string query, CancellationToken ct);

    /// <summary>
    /// Fetch <paramref name="url"/> and return its (bounded) body when it is
    /// readable text, or a non-readable marker otherwise.
    /// </summary>
    Task<FetchedPage> FetchPageAsync(string url, CancellationToken ct);
}
