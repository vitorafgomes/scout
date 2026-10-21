using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Weather.Abstractions;

namespace Scout.Plugin.Weather;

/// <summary>
/// Read-only weather plugin exposed to the LLM as Semantic Kernel functions,
/// backed by Open-Meteo (keyless). Each method is a
/// <see cref="KernelFunctionAttribute"/> the model can invoke by name. Every call
/// returns a non-empty, user-safe string; transport and API failures are caught
/// and surfaced as a clean error line so a tool call never throws back into the
/// kernel. Invalid input (missing city, an out-of-range day) returns a corrective
/// sentence rather than throwing.
/// </summary>
public sealed class WeatherPlugin
{
    private readonly IWeatherClient _client;
    private readonly TimeProvider _clock;
    private readonly string? _defaultCity;
    private readonly string _timeZone;

    /// <summary>Creates the plugin over a read-only weather client.</summary>
    /// <param name="client">The weather client. Must be non-null.</param>
    /// <param name="timeProvider">Clock used to resolve relative days. Must be non-null.</param>
    /// <param name="options">Plugin options (default city, time zone). Must be non-null.</param>
    public WeatherPlugin(
        IWeatherClient client,
        TimeProvider timeProvider,
        IOptions<WeatherOptions> options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _defaultCity = value.DefaultCity;
        _timeZone = string.IsNullOrWhiteSpace(value.TimeZone) ? TimeZoneInfo.Local.Id : value.TimeZone;
    }

    /// <summary>Get the current weather for a city.</summary>
    /// <param name="city">City to look up.</param>
    /// <param name="ct">Cancels the underlying requests.</param>
    /// <returns>A one-line summary, or a clean message when unknown or on failure.</returns>
    [KernelFunction("get_current_weather")]
    [Description("Get the current weather for a city: temperature, feels-like, sky condition, wind, and precipitation right now.")]
    public async Task<string> GetCurrentWeatherAsync(
        [Description("City name to look up, e.g. London.")] string city,
        CancellationToken ct)
    {
        var resolved = ResolveCity(city);
        if (resolved is null)
        {
            return NoCityHint();
        }

        try
        {
            var place = await _client.FindPlaceAsync(resolved, ct).ConfigureAwait(false);
            if (place is null)
            {
                return $"No place named {resolved}.";
            }

            var w = await _client
                .GetCurrentWeatherAsync(place.Latitude, place.Longitude, ct)
                .ConfigureAwait(false);

            return
                $"Weather in {PlaceLabel(place)}: {Num(w.Temperature)}{w.TemperatureUnit} " +
                $"(feels like {Num(w.ApparentTemperature)}{w.TemperatureUnit}), {Sky(w.WeatherCode)}, " +
                $"wind {Num(w.WindSpeed)} {w.WindSpeedUnit}, precipitation {Num(w.Precipitation)} {w.PrecipitationUnit}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Get the daily forecast for a city on a given day.</summary>
    /// <param name="city">City to look up.</param>
    /// <param name="day">Day to forecast: today, tomorrow, or a weekday name.</param>
    /// <param name="ct">Cancels the underlying requests.</param>
    /// <returns>A one-line forecast, a corrective hint, or a clean message on failure.</returns>
    [KernelFunction("get_forecast")]
    [Description("Get the daily forecast for a city on a given day: min/max temperature, rain probability, sunrise and sunset. The day can be 'today', 'tomorrow', or a weekday name, and must fall within today through the next 7 days.")]
    public async Task<string> GetForecastAsync(
        [Description("City name to look up, e.g. London.")] string city,
        [Description("Day to forecast: 'today', 'tomorrow', or a weekday name like 'friday'. Defaults to today.")] string day = "today",
        CancellationToken ct = default)
    {
        var resolved = ResolveCity(city);
        if (resolved is null)
        {
            return NoCityHint();
        }

        if (!RelaxedDate.TryParse(day, _clock, _timeZone, out var target))
        {
            return $"I could not understand the day '{day}'. {RelaxedDate.AcceptedFormatsHint()}";
        }

        var today = RelaxedDate.Today(_clock, _timeZone);
        if (target < today || target > today.AddDays(7))
        {
            return $"I can only forecast today through the next 7 days. {RelaxedDate.AcceptedFormatsHint()}";
        }

        try
        {
            var place = await _client.FindPlaceAsync(resolved, ct).ConfigureAwait(false);
            if (place is null)
            {
                return $"No place named {resolved}.";
            }

            var days = await _client
                .GetDailyForecastAsync(place.Latitude, place.Longitude, ct)
                .ConfigureAwait(false);
            var match = days.FirstOrDefault(d => d.Date == target);
            if (match is null)
            {
                return $"No forecast available for {PlaceLabel(place)} on {target:yyyy-MM-dd}.";
            }

            return
                $"Forecast for {PlaceLabel(place)} on {match.Date:yyyy-MM-dd}: " +
                $"min {Num(match.MinTemperature)}{match.TemperatureUnit}, " +
                $"max {Num(match.MaxTemperature)}{match.TemperatureUnit}, " +
                $"rain probability {match.PrecipitationProbabilityMax}%, " +
                $"sunrise {TimePart(match.Sunrise)}, sunset {TimePart(match.Sunset)}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Get the current air quality for a city.</summary>
    /// <param name="city">City to look up.</param>
    /// <param name="ct">Cancels the underlying requests.</param>
    /// <returns>A one-line air-quality summary, or a clean message when unknown or on failure.</returns>
    [KernelFunction("get_air_quality")]
    [Description("Get the current air quality for a city as a European AQI value with a plain good/moderate/poor rating and a short note.")]
    public async Task<string> GetAirQualityAsync(
        [Description("City name to look up, e.g. London.")] string city,
        CancellationToken ct)
    {
        var resolved = ResolveCity(city);
        if (resolved is null)
        {
            return NoCityHint();
        }

        try
        {
            var place = await _client.FindPlaceAsync(resolved, ct).ConfigureAwait(false);
            if (place is null)
            {
                return $"No place named {resolved}.";
            }

            var air = await _client
                .GetAirQualityAsync(place.Latitude, place.Longitude, ct)
                .ConfigureAwait(false);
            if (air.EuropeanAqi is not double aqi)
            {
                return $"Air quality data is unavailable for {PlaceLabel(place)}.";
            }

            var rating = Rating(aqi);
            return $"Air quality in {PlaceLabel(place)}: European AQI {Num(aqi)} ({rating}) — {RatingNote(rating)}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private string? ResolveCity(string city)
    {
        if (!string.IsNullOrWhiteSpace(city))
        {
            return city.Trim();
        }

        return string.IsNullOrWhiteSpace(_defaultCity) ? null : _defaultCity.Trim();
    }

    private static string NoCityHint()
        => "Tell me which city you want, e.g. London.";

    private static string PlaceLabel(GeoLocation place)
        => string.IsNullOrWhiteSpace(place.Country) ? place.Name : $"{place.Name}, {place.Country}";

    private static string Num(double value)
        => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string TimePart(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return "unknown";
        }

        var t = iso.IndexOf('T', StringComparison.Ordinal);
        if (t < 0)
        {
            return iso;
        }

        var time = iso[(t + 1)..];
        return time.Length >= 5 ? time[..5] : time;
    }

    private static string Rating(double aqi) => aqi switch
    {
        <= 40 => "good",
        <= 60 => "moderate",
        _ => "poor",
    };

    private static string RatingNote(string rating) => rating switch
    {
        "good" => "little to no health risk",
        "moderate" => "acceptable for most people",
        _ => "sensitive groups should limit outdoor exposure",
    };

    private static string Sky(int weatherCode) => weatherCode switch
    {
        0 => "clear sky",
        1 => "mainly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 => "fog",
        48 => "depositing rime fog",
        51 => "light drizzle",
        53 => "moderate drizzle",
        55 => "dense drizzle",
        56 => "light freezing drizzle",
        57 => "dense freezing drizzle",
        61 => "slight rain",
        63 => "moderate rain",
        65 => "heavy rain",
        66 => "light freezing rain",
        67 => "heavy freezing rain",
        71 => "slight snowfall",
        73 => "moderate snowfall",
        75 => "heavy snowfall",
        77 => "snow grains",
        80 => "slight rain showers",
        81 => "moderate rain showers",
        82 => "violent rain showers",
        85 => "slight snow showers",
        86 => "heavy snow showers",
        95 => "thunderstorm",
        96 => "thunderstorm with slight hail",
        99 => "thunderstorm with heavy hail",
        _ => "unknown conditions",
    };

    private static bool IsHandledFailure(Exception ex)
        => ex is WeatherApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        WeatherApiException api => api.Message,
        OperationCanceledException => "Weather request timed out. Try again later.",
        _ => "Could not reach the weather service. Try again later.",
    };
}
