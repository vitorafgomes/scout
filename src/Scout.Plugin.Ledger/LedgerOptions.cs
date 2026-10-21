namespace Scout.Plugin.Ledger;

/// <summary>
/// Configuration for the Ledger plugin, bound from "Scout:Plugins:Ledger". This
/// is a keyless, store-only plugin, so it gates purely on <see cref="Enabled"/>.
/// </summary>
public sealed class LedgerOptions
{
    /// <summary>
    /// Whether the plugin is registered. Defaults to true; set false to disable
    /// the ledger without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Currency stored when the recorded amount carries no symbol or code, e.g.
    /// "GBP". Amounts are never converted; this only labels an otherwise bare
    /// number.
    /// </summary>
    public string DefaultCurrency { get; set; } = "GBP";

    /// <summary>
    /// IANA time zone used to bucket entries into calendar months and to stamp
    /// the current year, e.g. "Europe/London". Falls back to UTC when unknown.
    /// </summary>
    public string TimeZone { get; set; } = "Europe/London";
}
