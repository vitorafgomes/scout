namespace Scout.Plugin.Dictionary.Abstractions;

/// <summary>
/// Read-only access to English word definitions (dictionaryapi.dev). Every call is
/// a GET; no external state is ever mutated.
/// </summary>
public interface IDefinitionsClient
{
    /// <summary>
    /// Fetch the definitions of <paramref name="word"/> (a lowercase English word).
    /// Returns null when the word is unknown (404), so the caller can surface a
    /// friendly "not found" message rather than an error. Returns an empty list
    /// when the entry exists but carries no usable definition text.
    /// </summary>
    Task<IReadOnlyList<WordDefinition>?> DefineAsync(string word, CancellationToken ct);
}
