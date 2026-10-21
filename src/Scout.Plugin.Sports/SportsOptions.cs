namespace Scout.Plugin.Sports;

/// <summary>
/// Configuration for the Sports plugin, bound from "Scout:Plugins:Sports". The
/// plugin self-gates on <see cref="Enabled"/>; the underlying API works on a free
/// public test key, so the feature is on by default.
/// </summary>
public sealed class SportsOptions
{
    /// <summary>
    /// Whether the plugin is active. When false the registration is a clean no-op
    /// (no client, no kernel plugin). Defaults to true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional upgrade key for the sports data provider. Leave empty to use the
    /// free public test tier; supplying a key raises the rate limits. Never
    /// required.
    /// </summary>
    public string FootballDataToken { get; set; } = string.Empty;

    /// <summary>
    /// IANA time zone used to resolve the current football season. Empty (the
    /// default) means Europe/London.
    /// </summary>
    public string TimeZone { get; set; } = string.Empty;
}
