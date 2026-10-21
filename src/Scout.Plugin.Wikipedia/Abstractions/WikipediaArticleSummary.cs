namespace Scout.Plugin.Wikipedia.Abstractions;

/// <summary>
/// The introductory summary of a single Wikipedia article. Read-only projection
/// from the Wikimedia REST summary endpoint. <paramref name="Extract"/> is
/// hard-capped in length by the client to keep the LLM context small.
/// </summary>
/// <param name="Title">Article title, e.g. "Alan Turing".</param>
/// <param name="Extract">Plain-text intro summary (length-capped); may be empty.</param>
/// <param name="Url">Canonical desktop page URL, or empty when unavailable.</param>
public sealed record WikipediaArticleSummary(string Title, string Extract, string Url);
