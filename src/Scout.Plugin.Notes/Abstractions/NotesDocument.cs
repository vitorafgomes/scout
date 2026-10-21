namespace Scout.Plugin.Notes.Abstractions;

/// <summary>
/// The persisted shape of the "notes" collection in the Scout-local store. Holds
/// the running id counter and every saved note. One document per machine.
/// </summary>
public sealed class NotesDocument
{
    /// <summary>Next id to hand out. Ids are rendered as "n{NextId}" and increment.</summary>
    public int NextId { get; set; } = 1;

    /// <summary>All saved notes, in insertion order (oldest first).</summary>
    public List<NoteEntry> Notes { get; set; } = [];
}
