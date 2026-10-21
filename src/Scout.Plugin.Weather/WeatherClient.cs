using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.Weather.Abstractions;

namespace Scout.Plugin.Weather;

/// <summary>
/// Thin read-only Open-Meteo client over an injected, pre-configured
/// <see cref="HttpClient"/> (timeout and User-Agent are set by DI). Open-Meteo is
/// keyless and every call is a GET against absolute URLs. Non-success responses
/// and unreadable bodies become a user-safe <see cref="WeatherApiException"/>;
/// timeouts and transport faults bubble unchanged.
/// </summary>
internal sealed class WeatherClient(HttpClient http) : IWeatherClient
{
    private const string GeocodeUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    private const string AirQualityUrl = "https://air-quality-api.open-meteo.com/v1/air-quality";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<GeoLocation?> FindPlaceAsync(string city, CancellationToken ct)
    {
        var url = $"{GeocodeUrl}?name={Uri.EscapeDataString(city)}&count=1&language=en&format=json";
        var dto = await GetAsync<GeoResponseDto>(url, ct).ConfigureAwait(false);

        var first = dto.Results is { Count: > 0 } results ? results[0] : null;
        if (first is null || string.IsNullOrWhiteSpace(first.Name))
        {
            return null;
        }

        return new GeoLocation(first.Name, NullIfBlank(first.Country), first.Latitude, first.Longitude);
    }

    public async Task<CurrentWeather> GetCurrentWeatherAsync(
        double latitude, double longitude, CancellationToken ct)
    {
        var url =
            $"{ForecastUrl}?latitude={Coord(latitude)}&longitude={Coord(longitude)}" +
            "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,precipitation" +
            "&timezone=auto&forecast_days=1";

        var dto = await GetAsync<CurrentResponseDto>(url, ct).ConfigureAwait(false);
        var current = dto.Current
            ?? throw new WeatherApiException("The weather service returned no current data.");
        var units = dto.CurrentUnits;

        return new CurrentWeather(
            current.Temperature,
            current.ApparentTemperature,
            current.WeatherCode,
            current.WindSpeed,
            current.Precipitation,
            NullIfBlank(units?.Temperature) ?? "°C",
            NullIfBlank(units?.WindSpeed) ?? "km/h",
            NullIfBlank(units?.Precipitation) ?? "mm");
    }

    public async Task<IReadOnlyList<DailyForecast>> GetDailyForecastAsync(
        double latitude, double longitude, CancellationToken ct)
    {
        var url =
            $"{ForecastUrl}?latitude={Coord(latitude)}&longitude={Coord(longitude)}" +
            "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset" +
            "&timezone=auto&forecast_days=8";

        var dto = await GetAsync<DailyResponseDto>(url, ct).ConfigureAwait(false);
        var daily = dto.Daily;
        if (daily?.Time is null)
        {
            return [];
        }

        var unit = NullIfBlank(dto.DailyUnits?.TemperatureMax) ?? "°C";
        var result = new List<DailyForecast>(daily.Time.Count);
        for (var i = 0; i < daily.Time.Count; i++)
        {
            if (!DateOnly.TryParseExact(
                    daily.Time[i], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                continue;
            }

            result.Add(new DailyForecast(
                date,
                At(daily.TemperatureMin, i, 0d),
                At(daily.TemperatureMax, i, 0d),
                ProbabilityAt(daily.PrecipitationProbabilityMax, i),
                At(daily.Sunrise, i, string.Empty),
                At(daily.Sunset, i, string.Empty),
                unit));
        }

        return result;
    }

    public async Task<AirQuality> GetAirQualityAsync(
        double latitude, double longitude, CancellationToken ct)
    {
        var url =
            $"{AirQualityUrl}?latitude={Coord(latitude)}&longitude={Coord(longitude)}" +
            "&current=european_aqi";

        var dto = await GetAsync<AirQualityResponseDto>(url, ct).ConfigureAwait(false);
        return new AirQuality(dto.Current?.EuropeanAqi);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new WeatherApiException(
                "The weather service returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new WeatherApiException(
                "The weather service returned an unreadable response.");
        }
    }

    private static WeatherApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests)
        {
            return new WeatherApiException(
                "Weather service rate limit reached. Try again later.", status);
        }

        if ((int)status >= 500)
        {
            return new WeatherApiException(
                "Weather service is currently unavailable. Try again later.", status);
        }

        if (status == HttpStatusCode.NotFound)
        {
            return new WeatherApiException(
                "Weather location data not found.", status);
        }

        return new WeatherApiException($"Weather request failed ({(int)status}).", status);
    }

    private static string Coord(double value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static T At<T>(List<T>? values, int index, T fallback)
        => values is not null && index < values.Count ? values[index] : fallback;

    private static int ProbabilityAt(List<int?>? values, int index)
        => values is not null && index < values.Count && values[index] is int v ? v : 0;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record GeoResponseDto(
        [property: JsonPropertyName("results")] List<GeoResultDto>? Results);

    private sealed record GeoResultDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("country")] string? Country,
        [property: JsonPropertyName("latitude")] double Latitude,
        [property: JsonPropertyName("longitude")] double Longitude);

    private sealed record CurrentResponseDto(
        [property: JsonPropertyName("current")] CurrentDto? Current,
        [property: JsonPropertyName("current_units")] CurrentUnitsDto? CurrentUnits);

    private sealed record CurrentDto(
        [property: JsonPropertyName("temperature_2m")] double Temperature,
        [property: JsonPropertyName("apparent_temperature")] double ApparentTemperature,
        [property: JsonPropertyName("weather_code")] int WeatherCode,
        [property: JsonPropertyName("wind_speed_10m")] double WindSpeed,
        [property: JsonPropertyName("precipitation")] double Precipitation);

    private sealed record CurrentUnitsDto(
        [property: JsonPropertyName("temperature_2m")] string? Temperature,
        [property: JsonPropertyName("wind_speed_10m")] string? WindSpeed,
        [property: JsonPropertyName("precipitation")] string? Precipitation);

    private sealed record DailyResponseDto(
        [property: JsonPropertyName("daily")] DailyDto? Daily,
        [property: JsonPropertyName("daily_units")] DailyUnitsDto? DailyUnits);

    private sealed record DailyDto(
        [property: JsonPropertyName("time")] List<string>? Time,
        [property: JsonPropertyName("temperature_2m_max")] List<double>? TemperatureMax,
        [property: JsonPropertyName("temperature_2m_min")] List<double>? TemperatureMin,
        [property: JsonPropertyName("precipitation_probability_max")] List<int?>? PrecipitationProbabilityMax,
        [property: JsonPropertyName("sunrise")] List<string>? Sunrise,
        [property: JsonPropertyName("sunset")] List<string>? Sunset);

    private sealed record DailyUnitsDto(
        [property: JsonPropertyName("temperature_2m_max")] string? TemperatureMax);

    private sealed record AirQualityResponseDto(
        [property: JsonPropertyName("current")] AirQualityCurrentDto? Current);

    private sealed record AirQualityCurrentDto(
        [property: JsonPropertyName("european_aqi")] double? EuropeanAqi);
}
