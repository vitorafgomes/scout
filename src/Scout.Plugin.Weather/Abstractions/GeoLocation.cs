namespace Scout.Plugin.Weather.Abstractions;

/// <summary>
/// A geocoded place: the resolved display name plus coordinates used for the
/// weather and air-quality lookups. Minimal projection of the first geocoding
/// result.
/// </summary>
/// <param name="Name">Resolved place name, e.g. "London".</param>
/// <param name="Country">Country name, or null when the source omits it.</param>
/// <param name="Latitude">Latitude in decimal degrees.</param>
/// <param name="Longitude">Longitude in decimal degrees.</param>
public sealed record GeoLocation(string Name, string? Country, double Latitude, double Longitude);
