namespace Scout.Plugin.Weather.Abstractions;

/// <summary>
/// Current conditions for a location. Read-only projection sourced from the
/// Open-Meteo forecast endpoint. The <see cref="WeatherCode"/> is a WMO code the
/// plugin maps to a short human phrase; unit strings come from the response so
/// the plugin never guesses.
/// </summary>
/// <param name="Temperature">Air temperature now.</param>
/// <param name="ApparentTemperature">Feels-like temperature now.</param>
/// <param name="WeatherCode">WMO weather interpretation code.</param>
/// <param name="WindSpeed">Wind speed at 10m.</param>
/// <param name="Precipitation">Precipitation in the current period.</param>
/// <param name="TemperatureUnit">Unit for temperature values, e.g. "°C".</param>
/// <param name="WindSpeedUnit">Unit for wind speed, e.g. "km/h".</param>
/// <param name="PrecipitationUnit">Unit for precipitation, e.g. "mm".</param>
public sealed record CurrentWeather(
    double Temperature,
    double ApparentTemperature,
    int WeatherCode,
    double WindSpeed,
    double Precipitation,
    string TemperatureUnit,
    string WindSpeedUnit,
    string PrecipitationUnit);
