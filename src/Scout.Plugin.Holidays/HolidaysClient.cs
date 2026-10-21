using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.Holidays.Abstractions;

namespace Scout.Plugin.Holidays;

/// <summary>
/// Thin read-only client over the public-holidays REST API (Nager.Date) using an
/// injected, pre-configured <see cref="HttpClient"/> (base address, timeout, and
/// User-Agent are set by DI). Non-success responses and unreadable bodies become a
/// user-safe <see cref="HolidaysApiException"/>; timeouts and transport faults
/// bubble unchanged. Every call is a GET.
/// </summary>
internal sealed class HolidaysClient(HttpClient http) : IHolidaysClient
{
    private const string WireDateFormat = "yyyy-MM-dd";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<PublicHoliday>> GetNextHolidaysAsync(
        string country, CancellationToken ct)
    {
        var items = await GetAsync<List<HolidayDto>>(
            $"api/v3/NextPublicHolidays/{country}", ct).ConfigureAwait(false);
        return items.Select(MapHoliday).ToList();
    }

    public async Task<IReadOnlyList<PublicHoliday>> GetHolidaysForYearAsync(
        int year, string country, CancellationToken ct)
    {
        var items = await GetAsync<List<HolidayDto>>(
            $"api/v3/PublicHolidays/{year}/{country}", ct).ConfigureAwait(false);
        return items.Select(MapHoliday).ToList();
    }

    public async Task<IReadOnlyList<LongWeekend>> GetLongWeekendsAsync(
        int year, string country, CancellationToken ct)
    {
        var items = await GetAsync<List<LongWeekendDto>>(
            $"api/v3/LongWeekend/{year}/{country}", ct).ConfigureAwait(false);
        return items.Select(MapLongWeekend).ToList();
    }

    private static PublicHoliday MapHoliday(HolidayDto dto) => new(
        ParseDate(dto.Date),
        dto.LocalName ?? string.Empty,
        NonEmptyName(dto.Name, dto.LocalName));

    private static LongWeekend MapLongWeekend(LongWeekendDto dto) => new(
        ParseDate(dto.StartDate),
        ParseDate(dto.EndDate),
        dto.DayCount,
        dto.NeedBridgeDay);

    private static string NonEmptyName(string? name, string? localName)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return string.IsNullOrWhiteSpace(localName) ? "Public holiday" : localName;
    }

    private static DateOnly ParseDate(string? value)
        => DateOnly.TryParseExact(
                value, WireDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new HolidaysApiException(
                "The holidays service returned an unreadable response.");

    /// <summary>
    /// GET that maps non-success responses and unreadable bodies to
    /// <see cref="HolidaysApiException"/>. Timeouts and transport faults bubble
    /// unchanged so the plugin can distinguish them.
    /// </summary>
    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new HolidaysApiException(
                "The holidays service returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new HolidaysApiException(
                "The holidays service returned an unreadable response.");
        }
    }

    private static HolidaysApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.NotFound:
                return new HolidaysApiException(
                    "Unknown country code. Use ISO codes like BR, GB, PT.", status);
            case HttpStatusCode.TooManyRequests:
                return new HolidaysApiException(
                    "The holidays service is rate limiting requests. Try again later.", status);
            default:
                if ((int)status >= 500)
                {
                    return new HolidaysApiException(
                        "The holidays service is currently unavailable. Try again later.", status);
                }

                return new HolidaysApiException(
                    $"The holidays request failed ({(int)status}).", status);
        }
    }

    private sealed record HolidayDto(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("localName")] string? LocalName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("countryCode")] string? CountryCode);

    private sealed record LongWeekendDto(
        [property: JsonPropertyName("startDate")] string? StartDate,
        [property: JsonPropertyName("endDate")] string? EndDate,
        [property: JsonPropertyName("dayCount")] int DayCount,
        [property: JsonPropertyName("needBridgeDay")] bool NeedBridgeDay);
}
