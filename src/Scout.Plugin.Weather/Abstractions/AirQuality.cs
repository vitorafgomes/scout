namespace Scout.Plugin.Weather.Abstractions;

/// <summary>
/// Current air quality for a location. Read-only projection sourced from the
/// Open-Meteo air-quality endpoint. <see cref="EuropeanAqi"/> is null when the
/// source has no value for the location.
/// </summary>
/// <param name="EuropeanAqi">Current European Air Quality Index, or null when unavailable.</param>
public sealed record AirQuality(double? EuropeanAqi);
