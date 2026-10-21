namespace Scout.Plugin.Weather.Abstractions;

/// <summary>
/// A single day's forecast. Read-only projection sourced from the Open-Meteo
/// daily forecast endpoint. Sunrise/sunset are local ISO-8601 timestamps the
/// plugin renders as a time of day.
/// </summary>
/// <param name="Date">The calendar day, in the location's time zone.</param>
/// <param name="MinTemperature">Minimum temperature for the day.</param>
/// <param name="MaxTemperature">Maximum temperature for the day.</param>
/// <param name="PrecipitationProbabilityMax">Maximum precipitation probability as a percentage.</param>
/// <param name="Sunrise">Local sunrise timestamp, e.g. "2026-07-03T05:12".</param>
/// <param name="Sunset">Local sunset timestamp, e.g. "2026-07-03T21:20".</param>
/// <param name="TemperatureUnit">Unit for temperature values, e.g. "°C".</param>
public sealed record DailyForecast(
    DateOnly Date,
    double MinTemperature,
    double MaxTemperature,
    int PrecipitationProbabilityMax,
    string Sunrise,
    string Sunset,
    string TemperatureUnit);
