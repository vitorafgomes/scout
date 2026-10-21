namespace Scout.Plugin.Notes;

/// <summary>
/// Configuration for the Notes plugin, bound from "Scout:Plugins:Notes". This is
/// a keyless, store-only plugin, so it gates purely on <see cref="Enabled"/>.
/// </summary>
public sealed class NotesOptions
{
    /// <summary>
    /// Whether the plugin is registered. Defaults to true; set false to disable
    /// note capture without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
