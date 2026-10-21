namespace Scout.Plugin.Dictionary.Abstractions;

/// <summary>
/// A single dictionary definition for an English word. Minimal read-only
/// projection from the definitions API; free-text fields are length-capped and
/// whitespace-collapsed by the client so values are relayed strictly as data.
/// </summary>
/// <param name="PartOfSpeech">Grammatical category, e.g. "noun" or "verb".</param>
/// <param name="Text">The definition text (length-capped, single line).</param>
/// <param name="Example">An illustrative usage example, or empty when none.</param>
public sealed record WordDefinition(string PartOfSpeech, string Text, string Example);
