namespace Scout.Plugin.Reminders.Abstractions;

/// <summary>
/// The persisted shape of the "reminders" collection in the Scout-local store.
/// Holds the running id counter and every stored reminder. One document per
/// machine.
/// </summary>
public sealed class RemindersDocument
{
    /// <summary>Next id to hand out. Ids are rendered as "r{NextId}" and increment.</summary>
    public int NextId { get; set; } = 1;

    /// <summary>All stored reminders, in insertion order (oldest first).</summary>
    public List<ReminderEntry> Reminders { get; set; } = [];
}
