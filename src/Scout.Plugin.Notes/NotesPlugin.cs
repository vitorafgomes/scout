using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Notes.Abstractions;

namespace Scout.Plugin.Notes;

/// <summary>
/// Quick personal note capture exposed to the LLM as Semantic Kernel functions.
/// Descriptive-write only: every operation reads or writes the Scout-local
/// "notes" collection and nothing external. Invalid model input returns a short
/// corrective sentence, and storage failures are caught and surfaced as a clean
/// error line, so a tool call never throws back into the kernel.
/// </summary>
public sealed class NotesPlugin
{
    private const string Collection = "notes";
    private const int SnippetLength = 80;
    private static readonly char[] LineBreaks = ['\n', '\r'];

    private readonly IScoutLocalStore _store;
    private readonly TimeProvider _clock;

    /// <summary>Creates the plugin over the local store and an injected clock.</summary>
    /// <param name="store">The Scout-local store. Must be non-null.</param>
    /// <param name="clock">Clock used to stamp note creation time. Must be non-null.</param>
    public NotesPlugin(IScoutLocalStore store, TimeProvider clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Save a short personal note, optionally tagged.</summary>
    /// <param name="text">The note body.</param>
    /// <param name="tags">Optional comma-separated tags.</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation line, a corrective hint on empty input, or a clean error line on failure.</returns>
    [KernelFunction("add_note")]
    [Description("Save a short personal note to Scout's local store, optionally tagged.")]
    public async Task<string> AddNoteAsync(
        [Description("The note text to save, e.g. \"Call the dentist on Monday\".")] string text,
        [Description("Optional comma-separated tags, e.g. \"health, urgent\". Leave empty for no tags.")] string tags = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Provide the note text to save, for example: add_note text=\"Buy milk tomorrow\".";
        }

        try
        {
            var normalizedTags = NormalizeTags(tags);
            var id = string.Empty;
            await _store.MutateAsync<NotesDocument>(Collection, doc =>
            {
                id = "n" + doc.NextId.ToString(CultureInfo.InvariantCulture);
                doc.NextId++;
                doc.Notes.Add(new NoteEntry
                {
                    Id = id,
                    Text = text.Trim(),
                    Tags = normalizedTags,
                    CreatedUtc = _clock.GetUtcNow(),
                });
            }, ct).ConfigureAwait(false);

            var tagsDisplay = normalizedTags.Length == 0 ? "none" : normalizedTags;
            return $"Note {id} saved (tags: {tagsDisplay}).";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Find saved notes whose text or tags contain a term.</summary>
    /// <param name="query">Word or phrase to look for, case-insensitive.</param>
    /// <param name="ct">Cancels the underlying store read.</param>
    /// <returns>One matching note per line, a no-match sentence, a corrective hint on empty input, or a clean error line.</returns>
    [KernelFunction("search_notes")]
    [Description("Find saved notes whose text or tags contain a word or phrase (case-insensitive).")]
    public async Task<string> SearchNotesAsync(
        [Description("The word or phrase to look for, e.g. \"dentist\".")] string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Provide a word to search for, for example: search_notes query=\"milk\".";
        }

        try
        {
            var document = await _store.ReadOrCreateAsync<NotesDocument>(Collection, ct).ConfigureAwait(false);
            var term = query.Trim();
            var matches = document.Notes
                .Where(n =>
                    n.Text.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    n.Tags.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Reverse()
                .ToList();

            return matches.Count == 0
                ? $"No notes match \"{term}\"."
                : string.Join("\n", matches.Select(FormatLine));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List the most recently saved notes, newest first.</summary>
    /// <param name="count">How many to show; parsed defensively and clamped to 1..50.</param>
    /// <param name="ct">Cancels the underlying store read.</param>
    /// <returns>One note per line, an empty sentence when none exist, or a clean error line on failure.</returns>
    [KernelFunction("list_recent_notes")]
    [Description("List the most recently saved notes, newest first.")]
    public async Task<string> ListRecentNotesAsync(
        [Description("How many notes to show, from 1 to 50. Defaults to 5, e.g. \"10\".")] string count = "5",
        CancellationToken ct = default)
    {
        try
        {
            var document = await _store.ReadOrCreateAsync<NotesDocument>(Collection, ct).ConfigureAwait(false);
            if (document.Notes.Count == 0)
            {
                return "No notes yet.";
            }

            var take = ParseCount(count);
            var recent = document.Notes.AsEnumerable().Reverse().Take(take).ToList();
            return string.Join("\n", recent.Select(FormatLine));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Delete a saved note by id.</summary>
    /// <param name="id">The id of the note to delete, e.g. "n1".</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation, a not-found hint listing existing ids, a corrective hint on empty input, or a clean error line.</returns>
    [KernelFunction("delete_note")]
    [Description("Delete a saved note by its id.")]
    public async Task<string> DeleteNoteAsync(
        [Description("The id of the note to delete, e.g. \"n1\".")] string id,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Provide the note id to delete, for example: delete_note id=\"n1\".";
        }

        try
        {
            var input = id.Trim();
            string? deletedId = null;
            var remainingIds = new List<string>();
            await _store.MutateAsync<NotesDocument>(Collection, doc =>
            {
                var match = doc.Notes.FirstOrDefault(n => IdMatches(n.Id, input));
                if (match is not null)
                {
                    deletedId = match.Id;
                    doc.Notes.Remove(match);
                }

                remainingIds = doc.Notes.Select(n => n.Id).ToList();
            }, ct).ConfigureAwait(false);

            if (deletedId is not null)
            {
                return $"Note {deletedId} deleted.";
            }

            return remainingIds.Count == 0
                ? $"No note \"{input}\". You have no notes yet."
                : $"No note \"{input}\". Existing ids: {string.Join(", ", remainingIds)}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string FormatLine(NoteEntry note)
    {
        var date = note.CreatedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var snippet = Snippet(note.Text);
        return note.Tags.Length == 0
            ? $"{note.Id}  {date}  {snippet}"
            : $"{note.Id}  {date}  {snippet}  [{note.Tags}]";
    }

    private static string Snippet(string text)
    {
        var index = text.IndexOfAny(LineBreaks);
        var firstLine = (index >= 0 ? text[..index] : text).Trim();
        return firstLine.Length <= SnippetLength
            ? firstLine
            : firstLine[..SnippetLength] + "...";
    }

    private static string NormalizeTags(string tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return string.Empty;
        }

        var parts = tags.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(", ", parts);
    }

    private static int ParseCount(string count)
    {
        if (!int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            value = 5;
        }

        return Math.Clamp(value, 1, 50);
    }

    // Accept the canonical id ("n1") case-insensitively, and the bare number ("1")
    // for convenience.
    private static bool IdMatches(string storedId, string input)
    {
        if (string.Equals(storedId, input, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return storedId.StartsWith('n') &&
               string.Equals(storedId[1..], input, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHandledFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        OperationCanceledException => "The note operation timed out. Try again.",
        _ => "Could not access local notes storage. Try again.",
    };
}
