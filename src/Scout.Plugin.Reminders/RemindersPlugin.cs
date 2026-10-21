using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Reminders.Abstractions;

namespace Scout.Plugin.Reminders;

/// <summary>
/// Personal reminders exposed to the LLM as Semantic Kernel functions.
/// Descriptive-write only: every operation reads or writes the Scout-local
/// "reminders" collection and nothing external. This is a pull-based store with
/// no proactive pings; recurrence is captured for display, not acted upon.
/// Invalid model input returns a short corrective sentence, a corrupted store is
/// recovered to empty on read, and storage failures are caught and surfaced as a
/// clean error line, so a tool call never throws back into the kernel.
/// </summary>
public sealed class RemindersPlugin
{
    private const string Collection = "reminders";

    private static readonly HashSet<string> EveryWords =
        new(StringComparer.OrdinalIgnoreCase) { "every", "each", "todo", "toda", "todos", "todas", "cada" };

    private static readonly HashSet<string> ArticleWords =
        new(StringComparer.OrdinalIgnoreCase) { "o", "a", "os", "as" };

    private static readonly HashSet<string> DayWords =
        new(StringComparer.OrdinalIgnoreCase) { "day", "days", "dia", "dias" };

    private static readonly HashSet<string> WeekWords =
        new(StringComparer.OrdinalIgnoreCase) { "week", "weeks", "semana", "semanas" };

    private static readonly HashSet<string> DailyWords =
        new(StringComparer.OrdinalIgnoreCase) { "daily", "everyday", "diariamente", "diario", "diária", "diaria" };

    private static readonly HashSet<string> WeeklyWords =
        new(StringComparer.OrdinalIgnoreCase) { "weekly", "semanalmente", "semanal" };

    private readonly IScoutLocalStore _store;
    private readonly TimeProvider _clock;
    private readonly string _timeZone;

    /// <summary>Creates the plugin over the local store, an injected clock and options.</summary>
    /// <param name="store">The Scout-local store. Must be non-null.</param>
    /// <param name="clock">Clock used to resolve relative dates and stamp times. Must be non-null.</param>
    /// <param name="options">Plugin options supplying the time zone. Must be non-null.</param>
    public RemindersPlugin(IScoutLocalStore store, TimeProvider clock, RemindersOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _timeZone = string.IsNullOrWhiteSpace(options.TimeZone) ? "Europe/London" : options.TimeZone;
    }

    /// <summary>Save a reminder with an optional time, capturing any recurrence.</summary>
    /// <param name="text">The reminder body.</param>
    /// <param name="when">A natural-language time, or empty for an undated reminder.</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation line, a corrective hint on bad input, or a clean error line on failure.</returns>
    [KernelFunction("add_reminder")]
    [Description("Save a personal reminder to Scout's local store. Understands times like tomorrow, friday 18:00, 25/12/2026, and recurring ones like every day 08:00.")]
    public async Task<string> AddReminderAsync(
        [Description("The reminder text to save, e.g. \"Call the dentist\".")] string text,
        [Description("When it is due, e.g. \"tomorrow 18:00\", \"friday\", \"25 dec\", or \"every day 08:00\". Leave empty for an undated reminder.")] string when = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Provide the reminder text to save, for example: add_reminder text=\"Call the dentist\" when=\"tomorrow 18:00\".";
        }

        DateOnly? dueDate = null;
        TimeOnly? dueTime = null;
        var recurrence = "none";
        if (!string.IsNullOrWhiteSpace(when))
        {
            if (!TryParseWhen(when, out dueDate, out dueTime, out recurrence))
            {
                return $"Could not read the time \"{when.Trim()}\". Try tomorrow, friday 18:00, 25/12/2026, or every day 08:00, or leave it empty for an undated reminder.";
            }
        }

        try
        {
            var id = string.Empty;
            await _store.MutateAsync<RemindersDocument>(Collection, doc =>
            {
                id = "r" + doc.NextId.ToString(CultureInfo.InvariantCulture);
                doc.NextId++;
                doc.Reminders.Add(new ReminderEntry
                {
                    Id = id,
                    Text = text.Trim(),
                    DueDate = dueDate,
                    DueTime = dueTime,
                    Recurrence = recurrence,
                    Completed = false,
                    CreatedUtc = _clock.GetUtcNow(),
                });
            }, ct).ConfigureAwait(false);

            var suffix = recurrence == "none" ? string.Empty : $" ({recurrence})";
            return dueDate is null
                ? $"Reminder {id} saved{suffix}."
                : $"Reminder {id} saved for {FormatDue(dueDate, dueTime)}{suffix}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List stored reminders, filtered by state.</summary>
    /// <param name="filter">"open" (default), "done", or "all".</param>
    /// <param name="ct">Cancels the underlying store read.</param>
    /// <returns>One reminder per line, an explicit empty sentence, a corrective hint, or a clean error line.</returns>
    [KernelFunction("list_reminders")]
    [Description("List stored reminders. Filter by \"open\" (not done, the default), \"done\", or \"all\".")]
    public async Task<string> ListRemindersAsync(
        [Description("Which reminders to show: \"open\", \"done\", or \"all\". Defaults to \"open\".")] string filter = "open",
        CancellationToken ct = default)
    {
        if (!TryNormalizeFilter(filter, out var normalized))
        {
            return "Filter must be open, done, or all, for example: list_reminders filter=\"open\".";
        }

        try
        {
            var document = await ReadDocumentAsync(ct).ConfigureAwait(false);
            var selected = document.Reminders
                .Where(r => normalized switch
                {
                    "done" => r.Completed,
                    "all" => true,
                    _ => !r.Completed,
                })
                .OrderBy(r => r.DueDate ?? DateOnly.MaxValue)
                .ThenBy(r => r.DueTime ?? TimeOnly.MaxValue)
                .ThenBy(r => r.CreatedUtc)
                .ToList();

            if (selected.Count == 0)
            {
                return normalized switch
                {
                    "done" => "No completed reminders.",
                    "all" => "No reminders yet.",
                    _ => "No open reminders.",
                };
            }

            return string.Join("\n", selected.Select(FormatLine));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Mark a reminder as done by id.</summary>
    /// <param name="id">The id of the reminder to complete, e.g. "r1".</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation, an already-done note, a not-found hint, a corrective hint, or a clean error line.</returns>
    [KernelFunction("complete_reminder")]
    [Description("Mark a stored reminder as done by its id.")]
    public async Task<string> CompleteReminderAsync(
        [Description("The id of the reminder to complete, e.g. \"r1\".")] string id,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Provide the reminder id to complete, for example: complete_reminder id=\"r1\".";
        }

        try
        {
            var input = id.Trim();
            string? completedId = null;
            var alreadyDone = false;
            var existingIds = new List<string>();
            await _store.MutateAsync<RemindersDocument>(Collection, doc =>
            {
                var match = doc.Reminders.FirstOrDefault(r => IdMatches(r.Id, input));
                if (match is not null)
                {
                    if (match.Completed)
                    {
                        alreadyDone = true;
                        completedId = match.Id;
                    }
                    else
                    {
                        match.Completed = true;
                        match.CompletedUtc = _clock.GetUtcNow();
                        completedId = match.Id;
                    }
                }

                existingIds = doc.Reminders.Select(r => r.Id).ToList();
            }, ct).ConfigureAwait(false);

            if (completedId is not null)
            {
                return alreadyDone
                    ? $"Reminder {completedId} was already done."
                    : $"Reminder {completedId} marked done.";
            }

            return existingIds.Count == 0
                ? $"No reminder \"{input}\". You have no reminders yet."
                : $"No reminder \"{input}\". Existing ids: {string.Join(", ", existingIds)}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Delete a reminder by id.</summary>
    /// <param name="id">The id of the reminder to delete, e.g. "r1".</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation, a not-found hint listing existing ids, a corrective hint, or a clean error line.</returns>
    [KernelFunction("delete_reminder")]
    [Description("Delete a stored reminder by its id.")]
    public async Task<string> DeleteReminderAsync(
        [Description("The id of the reminder to delete, e.g. \"r1\".")] string id,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Provide the reminder id to delete, for example: delete_reminder id=\"r1\".";
        }

        try
        {
            var input = id.Trim();
            string? deletedId = null;
            var remainingIds = new List<string>();
            await _store.MutateAsync<RemindersDocument>(Collection, doc =>
            {
                var match = doc.Reminders.FirstOrDefault(r => IdMatches(r.Id, input));
                if (match is not null)
                {
                    deletedId = match.Id;
                    doc.Reminders.Remove(match);
                }

                remainingIds = doc.Reminders.Select(r => r.Id).ToList();
            }, ct).ConfigureAwait(false);

            if (deletedId is not null)
            {
                return $"Reminder {deletedId} deleted.";
            }

            return remainingIds.Count == 0
                ? $"No reminder \"{input}\". You have no reminders yet."
                : $"No reminder \"{input}\". Existing ids: {string.Join(", ", remainingIds)}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    // Read the document, recovering to an empty one when the local file is
    // corrupted rather than surfacing a parse error to the user.
    private async Task<RemindersDocument> ReadDocumentAsync(CancellationToken ct)
    {
        try
        {
            return await _store.ReadOrCreateAsync<RemindersDocument>(Collection, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return new RemindersDocument();
        }
    }

    private static string FormatLine(ReminderEntry reminder)
    {
        var line = $"{reminder.Id}  {FormatDue(reminder.DueDate, reminder.DueTime)}  {reminder.Text}";
        if (reminder.Recurrence != "none")
        {
            line += $"  [{reminder.Recurrence}]";
        }

        if (reminder.Completed)
        {
            line += "  (done)";
        }

        return line;
    }

    private static string FormatDue(DateOnly? date, TimeOnly? time)
    {
        if (date is null)
        {
            return "someday";
        }

        var day = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return time is null
            ? day
            : $"{day} {time.Value.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    private static bool TryNormalizeFilter(string filter, out string normalized)
    {
        var value = filter?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            normalized = "open";
            return true;
        }

        switch (value.ToLowerInvariant())
        {
            case "open":
            case "pending":
            case "aberto":
            case "abertos":
            case "abertas":
            case "pendente":
            case "pendentes":
                normalized = "open";
                return true;
            case "done":
            case "completed":
            case "complete":
            case "concluido":
            case "concluído":
            case "concluidos":
            case "concluídos":
            case "feito":
            case "feitos":
                normalized = "done";
                return true;
            case "all":
            case "todos":
            case "todas":
            case "tudo":
                normalized = "all";
                return true;
            default:
                normalized = string.Empty;
                return false;
        }
    }

    // Interpret a natural-language "when": pull off a HH:mm time and any
    // recurrence keywords, then hand the remaining day token to the shared
    // RelaxedDate parser. Never reimplements date parsing.
    private bool TryParseWhen(string when, out DateOnly? date, out TimeOnly? time, out string recurrence)
    {
        date = null;
        time = null;
        recurrence = "none";

        var tokens = when.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (TryParseTimeToken(tokens[i], out var parsedTime))
            {
                time = parsedTime;
                tokens.RemoveAt(i);
                break;
            }
        }

        recurrence = ExtractRecurrence(tokens);

        if (tokens.Count == 0)
        {
            // No explicit day: anchor to today when a time or recurrence was given.
            if (time is not null || recurrence != "none")
            {
                date = RelaxedDate.Today(_clock, _timeZone);
                return true;
            }

            return false;
        }

        var dateText = string.Join(' ', tokens);
        if (RelaxedDate.TryParse(dateText, _clock, _timeZone, out var parsedDate))
        {
            date = parsedDate;
            return true;
        }

        return false;
    }

    // Pull recurrence keywords out of the token list, returning "none", "daily"
    // or "weekly" and leaving the day tokens behind for RelaxedDate to resolve.
    private static string ExtractRecurrence(List<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return "none";
        }

        if (EveryWords.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
            if (tokens.Count > 0 && ArticleWords.Contains(tokens[0]))
            {
                tokens.RemoveAt(0);
            }

            if (tokens.Count == 0)
            {
                return "daily";
            }

            if (DayWords.Contains(tokens[0]))
            {
                tokens.RemoveAt(0);
                return "daily";
            }

            if (WeekWords.Contains(tokens[0]))
            {
                tokens.RemoveAt(0);
                return "weekly";
            }

            // "every friday" / "every 25/12": the remainder is the weekday anchor.
            return "weekly";
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (DailyWords.Contains(tokens[i]))
            {
                tokens.RemoveAt(i);
                return "daily";
            }

            if (WeeklyWords.Contains(tokens[i]))
            {
                tokens.RemoveAt(i);
                return "weekly";
            }
        }

        return "none";
    }

    private static bool TryParseTimeToken(string token, out TimeOnly time)
    {
        time = default;
        var colon = token.IndexOf(':');
        if (colon <= 0 || colon >= token.Length - 1)
        {
            return false;
        }

        var hourPart = token[..colon];
        var minutePart = token[(colon + 1)..];
        if (minutePart.Length is < 1 or > 2)
        {
            return false;
        }

        if (int.TryParse(hourPart, NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            && int.TryParse(minutePart, NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
            && hour is >= 0 and <= 23
            && minute is >= 0 and <= 59)
        {
            time = new TimeOnly(hour, minute);
            return true;
        }

        return false;
    }

    // Accept the canonical id ("r1") case-insensitively, and the bare number ("1")
    // for convenience.
    private static bool IdMatches(string storedId, string input)
    {
        if (string.Equals(storedId, input, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return storedId.StartsWith('r') &&
               string.Equals(storedId[1..], input, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHandledFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        OperationCanceledException => "The reminders operation timed out. Try again.",
        _ => "Could not access local reminders storage. Try again.",
    };
}
