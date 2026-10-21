namespace Scout.Plugin.Holidays;

/// <summary>
/// Configuration for the Holidays plugin, bound from "Scout:Plugins:Holidays".
/// The plugin self-gates on <see cref="Enabled"/>: when disabled the registration
/// is a clean no-op (no client, no kernel plugin), so an unconfigured host still
/// starts.
/// </summary>
public sealed class HolidaysOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true. Set to false to disable it
    /// without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// ISO 3166 country code used when a call omits one, e.g. "GB" or "BR".
    /// </summary>
    public string DefaultCountry { get; set; } = "GB";

    /// <summary>
    /// IANA time zone used to resolve relative dates such as "today" and to decide
    /// which long weekends still remain this year. Empty (the default) means the
    /// machine's local time zone.
    /// </summary>
    public string TimeZone { get; set; } = "";
}
