using System.ComponentModel;
using Microsoft.SemanticKernel;
using Scout.Plugin.Dictionary.Abstractions;

namespace Scout.Plugin.Dictionary;

/// <summary>
/// Read-only English dictionary plugin exposed to the LLM as Semantic Kernel
/// functions. Each method is a <see cref="KernelFunctionAttribute"/> the model can
/// invoke by name through function-calling. Definitions come from dictionaryapi.dev
/// and synonyms/spelling from Datamuse; every call is a GET and nothing is ever
/// mutated. The plugin is English-only in v1: non-English input yields a corrective
/// sentence routing the model to Wikipedia instead. Every call returns a non-empty,
/// user-safe string; an unknown word yields a friendly "not found" sentence, and
/// transport or API failures are caught and surfaced as a clean error line, so a
/// tool call never throws back into the kernel. Returned dictionary text is
/// untrusted external content relayed strictly as data.
/// </summary>
public sealed class DictionaryPlugin
{
    private const int MaxWordLength = 50;
    private const string NotAWordHint =
        "Provide a single English word using letters only, for example \"serendipity\".";

    private readonly IDefinitionsClient _definitions;
    private readonly IDatamuseClient _datamuse;

    /// <summary>Creates the plugin over the read-only definitions and thesaurus clients.</summary>
    /// <param name="definitions">Definitions client (dictionaryapi.dev). Must be non-null.</param>
    /// <param name="datamuse">Synonyms and spelling client (Datamuse). Must be non-null.</param>
    public DictionaryPlugin(IDefinitionsClient definitions, IDatamuseClient datamuse)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _datamuse = datamuse ?? throw new ArgumentNullException(nameof(datamuse));
    }

    /// <summary>Look up the dictionary definitions of an English word.</summary>
    /// <param name="word">The English word to define.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The definitions one per line, a friendly not-found sentence, a corrective hint, or a clean error.</returns>
    [KernelFunction("define_word")]
    [Description("Look up the dictionary definitions of an English word: part of speech, meaning, and an example when available. English words only; for non-English words use Wikipedia instead.")]
    public async Task<string> DefineWordAsync(
        [Description("The English word to define, e.g. serendipity. English words only.")] string word,
        CancellationToken ct = default)
    {
        if (!TryNormalizeWord(word, out var normalized, out var display, out var hint))
        {
            return hint;
        }

        try
        {
            var definitions = await _definitions.DefineAsync(normalized, ct).ConfigureAwait(false);
            if (definitions is null || definitions.Count == 0)
            {
                return $"No definition found for '{display}'. English words only; for other languages try Wikipedia.";
            }

            return string.Join("\n", definitions.Select(FormatDefinition));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List synonyms for an English word.</summary>
    /// <param name="word">The English word to find synonyms for.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The synonyms one per line, a friendly none-found sentence, a corrective hint, or a clean error.</returns>
    [KernelFunction("get_synonyms")]
    [Description("List synonyms for an English word, closest matches first, one per line. English words only; for non-English words use Wikipedia instead.")]
    public async Task<string> GetSynonymsAsync(
        [Description("The English word to find synonyms for, e.g. happy. English words only.")] string word,
        CancellationToken ct = default)
    {
        if (!TryNormalizeWord(word, out var normalized, out var display, out var hint))
        {
            return hint;
        }

        try
        {
            var synonyms = await _datamuse.GetSynonymsAsync(normalized, ct).ConfigureAwait(false);
            return synonyms.Count == 0
                ? $"No synonyms found for '{display}'."
                : string.Join("\n", synonyms);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Check the spelling of an English word and suggest corrections.</summary>
    /// <param name="word">The English word to check.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>A correct/misspelled sentence with suggestions, a corrective hint, or a clean error.</returns>
    [KernelFunction("check_spelling")]
    [Description("Check whether an English word is spelled correctly and suggest close corrections when it is not. English words only; for non-English words use Wikipedia instead.")]
    public async Task<string> CheckSpellingAsync(
        [Description("The English word to check, e.g. recieve. English words only.")] string word,
        CancellationToken ct = default)
    {
        if (!TryNormalizeWord(word, out var normalized, out var display, out var hint))
        {
            return hint;
        }

        try
        {
            var suggestions = await _datamuse.GetSpellingSuggestionsAsync(normalized, ct).ConfigureAwait(false);
            if (suggestions.Count == 0)
            {
                return $"No spelling suggestions found for '{display}'.";
            }

            if (string.Equals(suggestions[0], normalized, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{display}' is spelled correctly.";
            }

            return $"'{display}' may be misspelled. Did you mean:\n{string.Join("\n", suggestions)}";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string FormatDefinition(WordDefinition definition)
    {
        var example = string.IsNullOrWhiteSpace(definition.Example)
            ? string.Empty
            : $" (e.g. {definition.Example})";
        return $"{definition.PartOfSpeech}: {definition.Text}{example}";
    }

    /// <summary>
    /// Validate and normalize model input. On success yields the lowercase word for
    /// the API and a display form (original trimmed case) for user-facing replies.
    /// On failure yields a corrective teaching sentence: empty input asks for a
    /// word, non-English letters route to Wikipedia, and anything else asks for a
    /// single English word.
    /// </summary>
    private static bool TryNormalizeWord(string? word, out string normalized, out string display, out string hint)
    {
        normalized = string.Empty;
        display = string.Empty;
        hint = string.Empty;

        var trimmed = word?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            hint = "Provide a word, for example \"serendipity\".";
            return false;
        }

        if (trimmed.Length > MaxWordLength)
        {
            hint = NotAWordHint;
            return false;
        }

        var hasNonEnglishLetter = false;
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetter(ch) || ch is '-' or '\'')
            {
                continue;
            }

            if (!char.IsAscii(ch) && char.IsLetter(ch))
            {
                hasNonEnglishLetter = true;
                continue;
            }

            hint = NotAWordHint;
            return false;
        }

        if (hasNonEnglishLetter)
        {
            hint = $"Dictionary covers English words only. For '{trimmed}', try Wikipedia instead.";
            return false;
        }

        display = trimmed;
        normalized = trimmed.ToLowerInvariant();
        return true;
    }

    private static bool IsHandledFailure(Exception ex)
        => ex is DictionaryApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        DictionaryApiException api => api.Message,
        OperationCanceledException => "The dictionary request timed out. Try again later.",
        _ => "Could not reach the dictionary service. Try again later.",
    };
}
