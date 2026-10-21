namespace Scout.Plugin.News.Abstractions;

/// <summary>A single link saved to the local reading list.</summary>
public sealed class ReadingListEntry
{
    /// <summary>Short human-friendly id, e.g. "r1".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The saved link. Validated to be an absolute http(s) URL on save.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Optional title, cleaned and length-capped. Empty when omitted.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Save instant in UTC, taken from the injected clock.</summary>
    public DateTimeOffset SavedUtc { get; set; }
}
