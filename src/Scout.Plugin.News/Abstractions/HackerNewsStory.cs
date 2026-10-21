namespace Scout.Plugin.News.Abstractions;

/// <summary>
/// A single Hacker News story from the Algolia API. Minimal read-only projection.
/// The title and URL are untrusted external content and are surfaced only as inert
/// data.
/// </summary>
/// <param name="Title">The story headline, e.g. "Show HN: A tiny database".</param>
/// <param name="Url">The story URL; falls back to the HN item page when absent.</param>
/// <param name="Points">The story score on Hacker News.</param>
public sealed record HackerNewsStory(string Title, string Url, int Points);
