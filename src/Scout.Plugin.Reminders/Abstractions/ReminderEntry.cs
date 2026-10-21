namespace Scout.Plugin.Reminders.Abstractions;

/// <summary>A single stored reminder.</summary>
public sealed class ReminderEntry
{
    /// <summary>Short human-friendly id, e.g. "r1".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The reminder text as the user typed it, trimmed.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Due date in the configured time zone, or null for an undated reminder.</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>Due wall-clock time in the configured time zone, or null when no time was given.</summary>
    public TimeOnly? DueTime { get; set; }

    /// <summary>Recurrence captured from the input: "none", "daily", or "weekly".</summary>
    public string Recurrence { get; set; } = "none";

    /// <summary>Whether the reminder has been marked done.</summary>
    public bool Completed { get; set; }

    /// <summary>Creation instant in UTC, taken from the injected clock.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Completion instant in UTC, set when the reminder is marked done.</summary>
    public DateTimeOffset? CompletedUtc { get; set; }
}
