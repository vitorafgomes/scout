namespace Scout.Plugin.Ledger.Abstractions;

/// <summary>
/// A single append-only ledger entry: an expense or an income, stored exactly as
/// the user stated it. The amount and currency are never converted.
/// </summary>
public sealed class LedgerEntry
{
    /// <summary>Running id within its year document, rendered as "#{Id}".</summary>
    public int Id { get; set; }

    /// <summary>Either "expense" or "income".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The magnitude of the entry, rounded to two decimals.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO-like currency label as given, e.g. "GBP" or "BRL".</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Expense category. Empty for income entries.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Income source. Empty for expense entries.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Optional free-text note. Empty when none was given.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Creation instant in UTC, taken from the injected clock.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
}
