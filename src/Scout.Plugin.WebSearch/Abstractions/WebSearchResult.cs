namespace Scout.Plugin.WebSearch.Abstractions;

/// <summary>
/// A single web search hit returned by the configured SearXNG instance. Minimal
/// read-only projection. The title and snippet are untrusted external text and
/// must be fenced before they reach the model.
/// </summary>
/// <param name="Title">The result title, e.g. "Introduction to async/await".</param>
/// <param name="Url">The result URL, e.g. "https://example.com/article".</param>
/// <param name="Snippet">A short extract from the page, in the page's own words.</param>
public sealed record WebSearchResult(string Title, string Url, string Snippet);
