namespace Scout.Plugin.WorldClock;

/// <summary>
/// Configuration for the WorldClock plugin, bound from
/// "Scout:Plugins:WorldClock". The plugin self-gates on <see cref="Enabled"/>
/// (default true): when disabled the registration is a clean no-op (no kernel
/// plugin), so an unconfigured host still starts.
/// </summary>
public sealed class WorldClockOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true; set false to leave the
    /// feature out (no kernel plugin registered).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// IANA time zone reported when a "current time" call omits a city, e.g.
    /// "Europe/London". Empty (the default) means the machine's local time zone,
    /// read from the host the worker runs on.
    /// </summary>
    public string DefaultTimeZone { get; set; } = "";
}
