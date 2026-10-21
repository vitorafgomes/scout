namespace Scout.Plugin.News.Abstractions;

/// <summary>
/// The persisted shape of the "reading-list" collection in the Scout-local store.
/// Holds the running id counter and every saved link. One document per machine.
/// </summary>
public sealed class ReadingListDocument
{
    /// <summary>Next id to hand out. Ids are rendered as "r{NextId}" and increment.</summary>
    public int NextId { get; set; } = 1;

    /// <summary>All saved links, in insertion order (oldest first).</summary>
    public List<ReadingListEntry> Items { get; set; } = [];
}
