namespace Scout.Plugin.Notes.Abstractions;

/// <summary>A single saved note.</summary>
public sealed class NoteEntry
{
    /// <summary>Short human-friendly id, e.g. "n1".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The note body as the user typed it, trimmed.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Normalized, comma-separated tags. Empty when untagged.</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>Creation instant in UTC, taken from the injected clock.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
}
