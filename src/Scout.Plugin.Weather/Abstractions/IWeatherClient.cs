namespace Scout.Plugin.Weather.Abstractions;

/// <summary>
/// Read-only access to weather data via Open-Meteo (no API key). Every call is a
/// GET: geocoding a place, and reading current conditions, the daily forecast,
/// and air quality. No mutation of any external state exists by design.
/// </summary>
public interface IWeatherClient
{
    /// <summary>
    /// Resolve <paramref name="city"/> to coordinates using the first geocoding
    /// result. Returns null when no place matches the name.
    /// </summary>
    Task<GeoLocation?> FindPlaceAsync(string city, CancellationToken ct);

    /// <summary>Read current conditions at the given coordinates.</summary>
    Task<CurrentWeather> GetCurrentWeatherAsync(
        double latitude, double longitude, CancellationToken ct);

    /// <summary>
    /// Read the daily forecast at the given coordinates for today plus the next
    /// seven days, ordered by date ascending.
    /// </summary>
    Task<IReadOnlyList<DailyForecast>> GetDailyForecastAsync(
        double latitude, double longitude, CancellationToken ct);

    /// <summary>Read current air quality at the given coordinates.</summary>
    Task<AirQuality> GetAirQualityAsync(
        double latitude, double longitude, CancellationToken ct);
}
