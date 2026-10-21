namespace Scout.Plugin.WorldClock.Abstractions;

/// <summary>
/// A city resolved from free text to its IANA time zone. <see cref="City"/> and
/// <see cref="Country"/> keep their display spelling (with diacritics) for
/// user-facing replies; <see cref="IanaId"/> is a tzdb identifier such as
/// "Europe/London" used to compute offsets via <see cref="TimeZoneInfo"/>.
/// </summary>
/// <param name="City">Display city name, e.g. "São Paulo".</param>
/// <param name="Country">Display country name, e.g. "Brazil".</param>
/// <param name="IanaId">IANA time zone id, e.g. "America/Sao_Paulo".</param>
public sealed record CityZone(string City, string Country, string IanaId);
