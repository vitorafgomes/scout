using Microsoft.Extensions.Options;
using Scout.Plugin.Weather;
using Scout.Plugin.Weather.Abstractions;
using Scout.Plugin.Weather.Tests.Fakes;

namespace Scout.Plugin.Weather.Tests;

public sealed class WeatherPluginTests
{
    // A fixed instant: 2026-07-03 09:00 UTC. In Europe/London (BST) the local
    // date is still 2026-07-03, which is the base for the forecast range tests.
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 3, 9, 0, 0, TimeSpan.Zero);

    private static IWeatherClient Client() => Substitute.For<IWeatherClient>();

    private static WeatherPlugin Plugin(
        IWeatherClient client,
        string? defaultCity = null,
        DateTimeOffset? now = null)
    {
        var clock = new FixedTimeProvider(now ?? FixedNow);
        var options = Options.Create(new WeatherOptions
        {
            DefaultCity = defaultCity,
            TimeZone = "Europe/London",
        });
        return new WeatherPlugin(client, clock, options);
    }

    private static GeoLocation London() => new("London", "United Kingdom", 51.5, -0.12);

    private static void GivenPlace(IWeatherClient client, string city, GeoLocation? place)
        => client.FindPlaceAsync(city, Arg.Any<CancellationToken>()).Returns(place);

    [Fact]
    public async Task GetCurrentWeather_Formats_Line()
    {
        var client = Client();
        GivenPlace(client, "London", London());
        client.GetCurrentWeatherAsync(51.5, -0.12, Arg.Any<CancellationToken>())
            .Returns(new CurrentWeather(18, 17, 2, 12, 0, "°C", "km/h", "mm"));

        var result = await Plugin(client).GetCurrentWeatherAsync("London", CancellationToken.None);

        result.ShouldContain("London, United Kingdom");
        result.ShouldContain("18°C");
        result.ShouldContain("feels like 17°C");
        result.ShouldContain("partly cloudy");
        result.ShouldContain("wind 12 km/h");
        result.ShouldContain("precipitation 0 mm");
    }

    [Fact]
    public async Task GetCurrentWeather_UnknownCity_Returns_Message()
    {
        var client = Client();
        GivenPlace(client, "Atlantis", place: null);

        var result = await Plugin(client).GetCurrentWeatherAsync("Atlantis", CancellationToken.None);

        result.ShouldBe("No place named Atlantis.");
    }

    [Fact]
    public async Task GetCurrentWeather_EmptyCity_NoDefault_Returns_Hint()
    {
        var client = Client();

        var result = await Plugin(client).GetCurrentWeatherAsync("   ", CancellationToken.None);

        result.ShouldBe("Tell me which city you want, e.g. London.");
    }

    [Fact]
    public async Task GetCurrentWeather_EmptyCity_Uses_DefaultCity()
    {
        var client = Client();
        GivenPlace(client, "Paris", new GeoLocation("Paris", "France", 48.85, 2.35));
        client.GetCurrentWeatherAsync(48.85, 2.35, Arg.Any<CancellationToken>())
            .Returns(new CurrentWeather(20, 20, 0, 8, 0, "°C", "km/h", "mm"));

        var result = await Plugin(client, defaultCity: "Paris")
            .GetCurrentWeatherAsync("", CancellationToken.None);

        result.ShouldContain("Paris, France");
        result.ShouldContain("clear sky");
    }

    [Fact]
    public async Task GetCurrentWeather_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.FindPlaceAsync("London", Arg.Any<CancellationToken>())
            .Returns<GeoLocation?>(_ => throw new WeatherApiException(
                "Weather service is currently unavailable. Try again later."));

        var plugin = Plugin(client);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetCurrentWeatherAsync("London", CancellationToken.None));

        result.ShouldBe("Weather service is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task GetCurrentWeather_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.FindPlaceAsync("London", Arg.Any<CancellationToken>())
            .Returns<GeoLocation?>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(client);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetCurrentWeatherAsync("London", CancellationToken.None));

        result.ShouldBe("Weather request timed out. Try again later.");
    }

    [Fact]
    public async Task GetCurrentWeather_Transport_Failure_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.FindPlaceAsync("London", Arg.Any<CancellationToken>())
            .Returns<GeoLocation?>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetCurrentWeatherAsync("London", CancellationToken.None));

        result.ShouldBe("Could not reach the weather service. Try again later.");
    }

    private static IReadOnlyList<DailyForecast> EightDaysFrom(DateOnly start)
    {
        var list = new List<DailyForecast>();
        for (var i = 0; i < 8; i++)
        {
            var date = start.AddDays(i);
            list.Add(new DailyForecast(
                date,
                MinTemperature: 12 + i,
                MaxTemperature: 20 + i,
                PrecipitationProbabilityMax: 10 * i,
                Sunrise: $"{date:yyyy-MM-dd}T05:12",
                Sunset: $"{date:yyyy-MM-dd}T21:20",
                TemperatureUnit: "°C"));
        }

        return list;
    }

    [Fact]
    public async Task GetForecast_Today_Formats_Line()
    {
        var client = Client();
        GivenPlace(client, "London", London());
        client.GetDailyForecastAsync(51.5, -0.12, Arg.Any<CancellationToken>())
            .Returns(EightDaysFrom(new DateOnly(2026, 7, 3)));

        var result = await Plugin(client)
            .GetForecastAsync("London", "today", CancellationToken.None);

        result.ShouldContain("Forecast for London, United Kingdom on 2026-07-03");
        result.ShouldContain("min 12°C");
        result.ShouldContain("max 20°C");
        result.ShouldContain("rain probability 0%");
        result.ShouldContain("sunrise 05:12");
        result.ShouldContain("sunset 21:20");
    }

    [Fact]
    public async Task GetForecast_Tomorrow_Picks_Next_Day()
    {
        var client = Client();
        GivenPlace(client, "London", London());
        client.GetDailyForecastAsync(51.5, -0.12, Arg.Any<CancellationToken>())
            .Returns(EightDaysFrom(new DateOnly(2026, 7, 3)));

        var result = await Plugin(client)
            .GetForecastAsync("London", "tomorrow", CancellationToken.None);

        result.ShouldContain("on 2026-07-04");
        result.ShouldContain("min 13°C");
        result.ShouldContain("max 21°C");
    }

    [Fact]
    public async Task GetForecast_Iso_Date_Within_Range_Picks_That_Day()
    {
        var client = Client();
        GivenPlace(client, "London", London());
        client.GetDailyForecastAsync(51.5, -0.12, Arg.Any<CancellationToken>())
            .Returns(EightDaysFrom(new DateOnly(2026, 7, 3)));

        var result = await Plugin(client)
            .GetForecastAsync("London", "2026-07-06", CancellationToken.None);

        result.ShouldContain("on 2026-07-06");
    }

    [Fact]
    public async Task GetForecast_Unparseable_Day_Returns_Corrective_Hint()
    {
        var client = Client();

        var result = await Plugin(client)
            .GetForecastAsync("London", "someday", CancellationToken.None);

        result.ShouldContain("could not understand the day 'someday'");
        result.ShouldContain(RelaxedDateHint);
        await client.DidNotReceive().FindPlaceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetForecast_OutOfRange_Day_Returns_Corrective_Hint()
    {
        var client = Client();

        // 2026-12-25 is far beyond today..+7 for the fixed clock (2026-07-03).
        var result = await Plugin(client)
            .GetForecastAsync("London", "2026-12-25", CancellationToken.None);

        result.ShouldContain("today through the next 7 days");
        result.ShouldContain(RelaxedDateHint);
        await client.DidNotReceive().FindPlaceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetForecast_UnknownCity_Returns_Message()
    {
        var client = Client();
        GivenPlace(client, "Atlantis", place: null);

        var result = await Plugin(client)
            .GetForecastAsync("Atlantis", "today", CancellationToken.None);

        result.ShouldBe("No place named Atlantis.");
    }

    [Fact]
    public async Task GetForecast_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        GivenPlace(client, "London", London());
        client.GetDailyForecastAsync(51.5, -0.12, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<DailyForecast>>(_ => throw new WeatherApiException(
                "Weather service rate limit reached. Try again later."));

        var plugin = Plugin(client);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetForecastAsync("London", "today", CancellationToken.None));

        result.ShouldBe("Weather service rate limit reached. Try again later.");
    }

    [Theory]
    [InlineData(15, "good")]
    [InlineData(55, "moderate")]
    [InlineData(85, "poor")]
    public async Task GetAirQuality_Maps_Rating(double aqi, string rating)
    {
        var client = Client();
        GivenPlace(client, "London", London());
        client.GetAirQualityAsync(51.5, -0.12, Arg.Any<CancellationToken>())
            .Returns(new AirQuality(aqi));

        var result = await Plugin(client).GetAirQualityAsync("London", CancellationToken.None);

        result.ShouldContain("London, United Kingdom");
        result.ShouldContain($"({rating})");
    }

    [Fact]
    public async Task GetAirQuality_Unavailable_Returns_Message()
    {
        var client = Client();
        GivenPlace(client, "London", London());
        client.GetAirQualityAsync(51.5, -0.12, Arg.Any<CancellationToken>())
            .Returns(new AirQuality(EuropeanAqi: null));

        var result = await Plugin(client).GetAirQualityAsync("London", CancellationToken.None);

        result.ShouldBe("Air quality data is unavailable for London, United Kingdom.");
    }

    [Fact]
    public async Task GetAirQuality_UnknownCity_Returns_Message()
    {
        var client = Client();
        GivenPlace(client, "Atlantis", place: null);

        var result = await Plugin(client).GetAirQualityAsync("Atlantis", CancellationToken.None);

        result.ShouldBe("No place named Atlantis.");
    }

    [Fact]
    public async Task GetAirQuality_Transport_Failure_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.FindPlaceAsync("London", Arg.Any<CancellationToken>())
            .Returns<GeoLocation?>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetAirQualityAsync("London", CancellationToken.None));

        result.ShouldBe("Could not reach the weather service. Try again later.");
    }

    [Fact]
    public void Ctor_Throws_When_Client_Null()
    {
        Should.Throw<ArgumentNullException>(() => new WeatherPlugin(
            null!, TimeProvider.System, Options.Create(new WeatherOptions())));
    }

    [Fact]
    public void Ctor_Throws_When_TimeProvider_Null()
    {
        Should.Throw<ArgumentNullException>(() => new WeatherPlugin(
            Substitute.For<IWeatherClient>(), null!, Options.Create(new WeatherOptions())));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new WeatherPlugin(
            Substitute.For<IWeatherClient>(), TimeProvider.System, null!));
    }

    private const string RelaxedDateHint =
        "Use a date like 2026-12-25 or 25/12/2026, or a word like today/tomorrow/friday";
}
