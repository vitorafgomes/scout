using System.Net;
using Scout.Plugin.Weather;
using Scout.Plugin.Weather.Tests.Fakes;

namespace Scout.Plugin.Weather.Tests;

/// <summary>
/// Covers <see cref="WeatherClient"/> field mapping and resilience at the
/// external HTTP boundary. Production-readiness requires at least one failing
/// call per boundary: timeout, 404, 429/rate-limit, 5xx, malformed body, and a
/// transport fault are each exercised here.
/// </summary>
public sealed class WeatherClientTests
{
    private static (WeatherClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        return (new WeatherClient(http), handler);
    }

    private const string GeoJson = """
        {
          "results": [
            { "name": "London", "country": "United Kingdom", "latitude": 51.5, "longitude": -0.12 }
          ]
        }
        """;

    private const string CurrentJson = """
        {
          "current": {
            "temperature_2m": 18.4,
            "apparent_temperature": 17.1,
            "weather_code": 2,
            "wind_speed_10m": 12.0,
            "precipitation": 0.0
          },
          "current_units": {
            "temperature_2m": "°C",
            "wind_speed_10m": "km/h",
            "precipitation": "mm"
          }
        }
        """;

    private const string DailyJson = """
        {
          "daily": {
            "time": ["2026-07-03","2026-07-04"],
            "temperature_2m_max": [21.0, 22.5],
            "temperature_2m_min": [12.0, 13.0],
            "precipitation_probability_max": [20, null],
            "sunrise": ["2026-07-03T05:12","2026-07-04T05:13"],
            "sunset": ["2026-07-03T21:20","2026-07-04T21:19"]
          },
          "daily_units": { "temperature_2m_max": "°C" }
        }
        """;

    private const string AirQualityJson = """
        { "current": { "european_aqi": 32.0 } }
        """;

    // ---- Happy-path mapping ------------------------------------------------

    [Fact]
    public async Task FindPlace_Parses_First_Result_And_Sends_Name()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GeoJson);

        var place = await client.FindPlaceAsync("London", CancellationToken.None);

        place.ShouldNotBeNull();
        place!.Name.ShouldBe("London");
        place.Country.ShouldBe("United Kingdom");
        place.Latitude.ShouldBe(51.5);
        place.Longitude.ShouldBe(-0.12);

        var url = handler.LastRequest!.RequestUri!.ToString();
        url.ShouldContain("geocoding-api.open-meteo.com");
        url.ShouldContain("name=London");
        url.ShouldContain("count=1");
    }

    [Fact]
    public async Task FindPlace_No_Results_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{}");

        var place = await client.FindPlaceAsync("Nowhereville", CancellationToken.None);

        place.ShouldBeNull();
    }

    [Fact]
    public async Task GetCurrentWeather_Parses_Fields_And_Units()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, CurrentJson);

        var w = await client.GetCurrentWeatherAsync(51.5, -0.12, CancellationToken.None);

        w.Temperature.ShouldBe(18.4);
        w.ApparentTemperature.ShouldBe(17.1);
        w.WeatherCode.ShouldBe(2);
        w.WindSpeed.ShouldBe(12.0);
        w.Precipitation.ShouldBe(0.0);
        w.TemperatureUnit.ShouldBe("°C");
        w.WindSpeedUnit.ShouldBe("km/h");
        w.PrecipitationUnit.ShouldBe("mm");

        var url = handler.LastRequest!.RequestUri!.ToString();
        url.ShouldContain("api.open-meteo.com/v1/forecast");
        url.ShouldContain("current=");
        url.ShouldContain("latitude=51.5");
        url.ShouldContain("longitude=-0.12");
    }

    [Fact]
    public async Task GetDailyForecast_Parses_Days_And_Requests_Eight()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, DailyJson);

        var days = await client.GetDailyForecastAsync(51.5, -0.12, CancellationToken.None);

        days.Count.ShouldBe(2);
        days[0].Date.ShouldBe(new DateOnly(2026, 7, 3));
        days[0].MinTemperature.ShouldBe(12.0);
        days[0].MaxTemperature.ShouldBe(21.0);
        days[0].PrecipitationProbabilityMax.ShouldBe(20);
        days[0].Sunrise.ShouldBe("2026-07-03T05:12");
        days[0].TemperatureUnit.ShouldBe("°C");
        // Null probability falls back to 0.
        days[1].PrecipitationProbabilityMax.ShouldBe(0);

        var url = handler.LastRequest!.RequestUri!.ToString();
        url.ShouldContain("daily=");
        url.ShouldContain("forecast_days=8");
    }

    [Fact]
    public async Task GetAirQuality_Parses_Aqi_And_Uses_AirQuality_Host()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, AirQualityJson);

        var air = await client.GetAirQualityAsync(51.5, -0.12, CancellationToken.None);

        air.EuropeanAqi.ShouldBe(32.0);

        var url = handler.LastRequest!.RequestUri!.ToString();
        url.ShouldContain("air-quality-api.open-meteo.com");
        url.ShouldContain("european_aqi");
    }

    // ---- Per-boundary failure modes ---------------------------------------

    [Fact]
    public async Task Geocode_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<WeatherApiException>(
            () => client.FindPlaceAsync("London", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Geocode_NotFound_Throws_NotFound_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        var ex = await Should.ThrowAsync<WeatherApiException>(
            () => client.FindPlaceAsync("London", CancellationToken.None));

        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task Current_RateLimited_Throws_RateLimit_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Should.ThrowAsync<WeatherApiException>(
            () => client.GetCurrentWeatherAsync(51.5, -0.12, CancellationToken.None));

        ex.Message.ShouldContain("rate limit");
    }

    [Fact]
    public async Task Daily_Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{ not valid json ");

        var ex = await Should.ThrowAsync<WeatherApiException>(
            () => client.GetDailyForecastAsync(51.5, -0.12, CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task AirQuality_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetAirQualityAsync(51.5, -0.12, CancellationToken.None));
    }

    [Fact]
    public async Task Current_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetCurrentWeatherAsync(51.5, -0.12, CancellationToken.None));
    }
}
