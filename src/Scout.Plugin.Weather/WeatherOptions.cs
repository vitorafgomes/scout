namespace Scout.Plugin.Weather;

/// <summary>
/// Configuration for the Weather plugin, bound from "Scout:Plugins:Weather".
/// The plugin self-gates on <see cref="Enabled"/>: when false the feature is a
/// clean no-op (no client, no kernel plugin registered).
/// </summary>
public sealed class WeatherOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true; set false to disable it
    /// without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// City used when a function is called without a city. Null or empty means a
    /// missing city yields a corrective hint instead.
    /// </summary>
    public string? DefaultCity { get; set; }

    /// <summary>
    /// IANA time zone used to resolve relative days (today/tomorrow/weekday) for
    /// the forecast. Empty (the default) means the machine's local time zone.
    /// </summary>
    public string TimeZone { get; set; } = "";
}
