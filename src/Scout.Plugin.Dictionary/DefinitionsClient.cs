using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.Dictionary.Abstractions;

namespace Scout.Plugin.Dictionary;

/// <summary>
/// Thin read-only client over the free English dictionary API (dictionaryapi.dev)
/// using an injected, pre-configured <see cref="HttpClient"/> (base address,
/// timeout, and User-Agent are set by DI). A 404 means the word is unknown and
/// yields null; other non-success responses and unreadable bodies become a
/// user-safe <see cref="DictionaryApiException"/>; timeouts and transport faults
/// bubble unchanged. Definition text is untrusted external content: it is
/// whitespace-collapsed and length-capped so values are relayed strictly as data.
/// Every call is a GET.
/// </summary>
internal sealed class DefinitionsClient(HttpClient http) : IDefinitionsClient
{
    private const int MaxDefinitions = 6;
    private const int MaxTextLength = 400;

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<WordDefinition>?> DefineAsync(string word, CancellationToken ct)
    {
        var path = $"api/v2/entries/en/{Uri.EscapeDataString(word)}";

        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);

        // A 404 means the word is not in the dictionary; surface it as an absent
        // result so the plugin can reply with a friendly "not found" sentence.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        List<EntryDto>? entries;
        try
        {
            entries = await response.Content
                .ReadFromJsonAsync<List<EntryDto>>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new DictionaryApiException(
                "The dictionary service returned an unreadable response.");
        }

        if (entries is null)
        {
            throw new DictionaryApiException(
                "The dictionary service returned an unexpected empty response.");
        }

        return Flatten(entries);
    }

    private static List<WordDefinition> Flatten(IEnumerable<EntryDto> entries)
    {
        var result = new List<WordDefinition>();
        foreach (var entry in entries)
        {
            foreach (var meaning in entry.Meanings ?? [])
            {
                var partOfSpeech = Normalize(meaning.PartOfSpeech);
                foreach (var definition in meaning.Definitions ?? [])
                {
                    var text = Normalize(definition.Definition);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    result.Add(new WordDefinition(
                        partOfSpeech.Length == 0 ? "definition" : partOfSpeech,
                        Cap(text),
                        Cap(Normalize(definition.Example))));

                    if (result.Count >= MaxDefinitions)
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }

    private static DictionaryApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests)
        {
            return new DictionaryApiException(
                "The dictionary service is rate limiting requests. Try again later.", status);
        }

        if ((int)status >= 500)
        {
            return new DictionaryApiException(
                "The dictionary service is currently unavailable. Try again later.", status);
        }

        return new DictionaryApiException(
            $"The dictionary request failed ({(int)status}).", status);
    }

    private static string Cap(string text)
        => text.Length <= MaxTextLength ? text : text[..MaxTextLength];

    /// <summary>
    /// Collapse all whitespace to single spaces and trim. External free text is
    /// surfaced only as inert single-line data.
    /// </summary>
    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return string.Join(' ', raw.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record EntryDto(
        [property: JsonPropertyName("word")] string? Word,
        [property: JsonPropertyName("meanings")] List<MeaningDto>? Meanings);

    private sealed record MeaningDto(
        [property: JsonPropertyName("partOfSpeech")] string? PartOfSpeech,
        [property: JsonPropertyName("definitions")] List<DefinitionDto>? Definitions);

    private sealed record DefinitionDto(
        [property: JsonPropertyName("definition")] string? Definition,
        [property: JsonPropertyName("example")] string? Example);
}
