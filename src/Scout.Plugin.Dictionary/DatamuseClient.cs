using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.Dictionary.Abstractions;

namespace Scout.Plugin.Dictionary;

/// <summary>
/// Thin read-only client over the keyless Datamuse word API using an injected,
/// pre-configured <see cref="HttpClient"/> (base address, timeout, and User-Agent
/// are set by DI). Serves synonyms (<c>rel_syn</c>) and spelling candidates
/// (<c>sp</c>). Non-success responses and unreadable bodies become a user-safe
/// <see cref="DictionaryApiException"/>; timeouts and transport faults bubble
/// unchanged. An empty result array is valid and yields an empty list. Returned
/// words are untrusted external content: they are trimmed and count-capped so
/// values are relayed strictly as data. Every call is a GET.
/// </summary>
internal sealed class DatamuseClient(HttpClient http) : IDatamuseClient
{
    private const int MaxSynonyms = 15;
    private const int MaxSpellingSuggestions = 8;

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<string>> GetSynonymsAsync(string word, CancellationToken ct)
    {
        var items = await GetWordsAsync(
            $"words?rel_syn={Uri.EscapeDataString(word)}&max={MaxSynonyms}", ct).ConfigureAwait(false);
        return Extract(items, MaxSynonyms);
    }

    public async Task<IReadOnlyList<string>> GetSpellingSuggestionsAsync(string word, CancellationToken ct)
    {
        var items = await GetWordsAsync(
            $"words?sp={Uri.EscapeDataString(word)}&max={MaxSpellingSuggestions}", ct).ConfigureAwait(false);
        return Extract(items, MaxSpellingSuggestions);
    }

    private async Task<List<WordDto>> GetWordsAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<List<WordDto>>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? [];
        }
        catch (JsonException)
        {
            throw new DictionaryApiException(
                "The thesaurus service returned an unreadable response.");
        }
    }

    private static IReadOnlyList<string> Extract(IEnumerable<WordDto> items, int max)
        => items
            .Where(i => !string.IsNullOrWhiteSpace(i.Word))
            .Select(i => i.Word!.Trim())
            .Take(max)
            .ToList();

    private static DictionaryApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests)
        {
            return new DictionaryApiException(
                "The thesaurus service is rate limiting requests. Try again later.", status);
        }

        if ((int)status >= 500)
        {
            return new DictionaryApiException(
                "The thesaurus service is currently unavailable. Try again later.", status);
        }

        return new DictionaryApiException(
            $"The thesaurus request failed ({(int)status}).", status);
    }

    private sealed record WordDto(
        [property: JsonPropertyName("word")] string? Word,
        [property: JsonPropertyName("score")] int? Score);
}
