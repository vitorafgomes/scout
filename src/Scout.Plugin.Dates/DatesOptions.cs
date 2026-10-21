namespace Scout.Plugin.Dates;

/// <summary>
/// Configuration for the Dates plugin, bound from "Scout:Plugins:Dates". The
/// plugin self-gates on <see cref="Enabled"/> (default true): when disabled the
/// registration is a clean no-op, so an unconfigured host still starts.
/// </summary>
public sealed class DatesOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true; set false to leave the
    /// feature out (no kernel plugin registered).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// IANA time zone used when a call omits one, e.g. "America/Sao_Paulo".
    /// Empty (the default) means the machine's local time zone, read from the
    /// host the worker runs on. Applied to <c>current_time</c>, <c>today</c>,
    /// and to resolving relative words in the other functions.
    /// </summary>
    public string DefaultTimeZone { get; set; } = "";
}
