namespace Scout.Plugin.Dictionary.Abstractions;

/// <summary>
/// Read-only access to the Datamuse word API for synonyms and spelling
/// suggestions. Every call is a GET; no external state is ever mutated.
/// </summary>
public interface IDatamuseClient
{
    /// <summary>
    /// Synonyms for <paramref name="word"/> (a lowercase English word), closest
    /// matches first. Returns an empty list when none are found.
    /// </summary>
    Task<IReadOnlyList<string>> GetSynonymsAsync(string word, CancellationToken ct);

    /// <summary>
    /// Spelling candidates for <paramref name="word"/> (a lowercase English word),
    /// closest matches first. When the word is spelled correctly it appears as the
    /// first candidate. Returns an empty list when nothing resembles the input.
    /// </summary>
    Task<IReadOnlyList<string>> GetSpellingSuggestionsAsync(string word, CancellationToken ct);
}
