namespace Scout.Plugin.News.Abstractions;

/// <summary>
/// A single entry parsed from an RSS or Atom feed. Minimal read-only projection.
/// The title and link are untrusted external content and are surfaced only as
/// inert data.
/// </summary>
/// <param name="Title">The headline text, e.g. "Markets rally after rate cut".</param>
/// <param name="Link">The article URL, or empty when the feed omits one.</param>
/// <param name="Published">Publication instant when the feed supplies a parseable one.</param>
public sealed record FeedItem(string Title, string Link, DateTimeOffset? Published);
