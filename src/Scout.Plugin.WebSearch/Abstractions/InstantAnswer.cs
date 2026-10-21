namespace Scout.Plugin.WebSearch.Abstractions;

/// <summary>
/// A DuckDuckGo Instant Answer projection: a short factual answer plus an
/// optional source URL. <see cref="Text"/> is empty when the service has no
/// instant answer for the query. The text is untrusted external content and must
/// be fenced before it reaches the model.
/// </summary>
/// <param name="Text">The answer text, or empty when none is available.</param>
/// <param name="Source">The source URL for the answer, or empty when none.</param>
public sealed record InstantAnswer(string Text, string Source);
