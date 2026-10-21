namespace Scout.Plugin.Wikipedia.Abstractions;

/// <summary>
/// A single Wikipedia search hit. Minimal projection. The <paramref name="Snippet"/>
/// is plain text: source markup and HTML entities are stripped by the client so
/// the value is safe to relay as data.
/// </summary>
/// <param name="Title">Article title, e.g. "Alan Turing".</param>
/// <param name="Snippet">One-line plain-text excerpt matching the query; may be empty.</param>
public sealed record WikipediaSearchResult(string Title, string Snippet);
