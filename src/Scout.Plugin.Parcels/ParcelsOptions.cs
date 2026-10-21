namespace Scout.Plugin.Parcels;

/// <summary>
/// Configuration for the Parcels plugin, bound from "Scout:Plugins:Parcels".
/// The plugin self-gates on <see cref="ApiKey"/>: an empty key leaves the whole
/// feature inactive (no client, no kernel plugin registered), so an unconfigured
/// host still starts.
/// </summary>
public sealed class ParcelsOptions
{
    /// <summary>
    /// API key for the parcel tracking provider (Ship24), sent as a Bearer
    /// credential. Empty disables the entire plugin.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// IANA time zone used to render "watching since" dates in the local watch
    /// list. Defaults to Europe/London. An invalid or empty value falls back to
    /// UTC.
    /// </summary>
    public string TimeZone { get; set; } = "Europe/London";
}
