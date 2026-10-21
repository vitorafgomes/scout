namespace Scout.Plugin.Reminders;

/// <summary>
/// Configuration for the Reminders plugin, bound from "Scout:Plugins:Reminders".
/// This is a keyless, store-only plugin, so it gates purely on <see cref="Enabled"/>.
/// </summary>
public sealed class RemindersOptions
{
    /// <summary>
    /// Whether the plugin is registered. Defaults to true; set false to disable
    /// reminder capture without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// IANA time zone used to resolve relative dates such as "tomorrow" and the
    /// next occurrence of a weekday. Empty (the default) means Europe/London.
    /// </summary>
    public string TimeZone { get; set; } = "";
}
