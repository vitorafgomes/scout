namespace Scout.Plugin.Ledger.Abstractions;

/// <summary>
/// The persisted shape of one year of the Scout-local ledger, stored as
/// "ledger-{year}.json". Holds the running id counter and every entry recorded in
/// that year, append-only and oldest first.
/// </summary>
public sealed class LedgerDocument
{
    /// <summary>The calendar year this document holds.</summary>
    public int Year { get; set; }

    /// <summary>Next id to hand out; ids are rendered as "#{NextId}" and increment.</summary>
    public int NextId { get; set; } = 1;

    /// <summary>All entries for the year, in insertion order (oldest first).</summary>
    public List<LedgerEntry> Entries { get; set; } = [];
}
