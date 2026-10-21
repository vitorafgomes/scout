namespace Scout.Plugin.News.Abstractions;

/// <summary>
/// Read-only access to a single RSS or Atom feed. Every operation is a GET; no
/// external state is ever mutated.
/// </summary>
public interface IFeedClient
{
    /// <summary>
    /// Fetch and parse <paramref name="feedUrl"/> (an absolute http(s) URL),
    /// returning its entries in the order the feed lists them (newest first for a
    /// well-formed feed). Both RSS and Atom are supported.
    /// </summary>
    Task<IReadOnlyList<FeedItem>> GetFeedAsync(string feedUrl, CancellationToken ct);
}
