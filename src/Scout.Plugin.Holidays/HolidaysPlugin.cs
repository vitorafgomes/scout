using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Holidays.Abstractions;

namespace Scout.Plugin.Holidays;

/// <summary>
/// Read-only public-holidays plugin exposed to the LLM as Semantic Kernel
/// functions. Each method is a <see cref="KernelFunctionAttribute"/> the model can
/// invoke by name through function-calling. All data comes from GET calls; no
/// external state is ever changed. Every call returns a non-empty, user-safe
/// string; invalid input yields a corrective sentence and transport or API
/// failures are caught and surfaced as a clean error line, so a tool call never
/// throws back into the kernel.
/// </summary>
public sealed class HolidaysPlugin
{
    private const string CountryHint = "Use ISO codes like BR, GB, PT.";

    private readonly IHolidaysClient _client;
    private readonly TimeProvider _clock;
    private readonly string _defaultCountry;
    private readonly string _timeZone;

    /// <summary>Creates the plugin over a read-only holidays client and a clock.</summary>
    /// <param name="client">The holidays client. Must be non-null.</param>
    /// <param name="clock">Clock used to resolve relative dates. Must be non-null.</param>
    /// <param name="options">Plugin options supplying the default country and time zone. Must be non-null.</param>
    public HolidaysPlugin(IHolidaysClient client, TimeProvider clock, HolidaysOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _defaultCountry = string.IsNullOrWhiteSpace(options.DefaultCountry) ? "GB" : options.DefaultCountry;
        _timeZone = string.IsNullOrWhiteSpace(options.TimeZone) ? TimeZoneInfo.Local.Id : options.TimeZone;
    }

    /// <summary>The next public holidays for a country.</summary>
    /// <param name="country">ISO 3166 country code.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Up to three upcoming holidays, one per line, or a clean message.</returns>
    [KernelFunction("get_next_holidays")]
    [Description("List the next public holidays for a country, with weekday, date, and how many days away each is.")]
    public async Task<string> GetNextHolidaysAsync(
        [Description("ISO 3166 country code, e.g. GB or BR.")] string country = "GB",
        CancellationToken ct = default)
    {
        var code = Normalize(country);
        if (!IsValidCountryCode(code))
        {
            return CountryHint;
        }

        try
        {
            var holidays = await _client.GetNextHolidaysAsync(code, ct).ConfigureAwait(false);
            if (holidays.Count == 0)
            {
                return $"No upcoming public holidays found for {code}.";
            }

            var today = RelaxedDate.Today(_clock, _timeZone);
            var lines = holidays
                .Take(3)
                .Select(h => $"{Weekday(h.Date)} {Iso(h.Date)} - {h.Name} ({DaysAway(h.Date, today)})");
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Whether a given date is a public holiday.</summary>
    /// <param name="date">Date to check.</param>
    /// <param name="country">ISO 3166 country code.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>A yes/no sentence, naming the holiday when yes, or a clean message.</returns>
    [KernelFunction("is_holiday")]
    [Description("Check whether a specific date is a public holiday in a country, naming the holiday when it is.")]
    public async Task<string> IsHolidayAsync(
        [Description("Date to check, e.g. 2026-12-25, 25/12/2026, today, tomorrow, or friday.")] string date,
        [Description("ISO 3166 country code, e.g. GB or BR.")] string country = "GB",
        CancellationToken ct = default)
    {
        var code = Normalize(country);
        if (!IsValidCountryCode(code))
        {
            return CountryHint;
        }

        if (!RelaxedDate.TryParse(date, _clock, _timeZone, out var target))
        {
            return RelaxedDate.AcceptedFormatsHint();
        }

        try
        {
            var holidays = await _client.GetHolidaysForYearAsync(target.Year, code, ct).ConfigureAwait(false);
            var matches = holidays.Where(h => h.Date == target).Select(h => h.Name).ToList();
            if (matches.Count == 0)
            {
                return $"No - {Iso(target)} is not a public holiday in {code}.";
            }

            return $"Yes - {Iso(target)} is {string.Join(" / ", matches)} in {code}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>All public holidays for a country in a given year.</summary>
    /// <param name="country">ISO 3166 country code.</param>
    /// <param name="year">Four-digit year.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Every holiday, one per line, or a corrective or clean message.</returns>
    [KernelFunction("list_holidays")]
    [Description("List all public holidays for a country in a given year, one per line with date, weekday, and name.")]
    public async Task<string> ListHolidaysAsync(
        [Description("ISO 3166 country code, e.g. GB or BR.")] string country,
        [Description("Four-digit year, e.g. 2026.")] string year,
        CancellationToken ct = default)
    {
        var code = Normalize(country);
        if (!IsValidCountryCode(code))
        {
            return CountryHint;
        }

        if (!TryParseYear(year, out var parsedYear))
        {
            return "Provide a 4-digit year like 2026.";
        }

        try
        {
            var holidays = await _client.GetHolidaysForYearAsync(parsedYear, code, ct).ConfigureAwait(false);
            if (holidays.Count == 0)
            {
                return $"No public holidays found for {code} in {parsedYear}.";
            }

            var lines = holidays
                .OrderBy(h => h.Date)
                .Select(h => $"{Iso(h.Date)} {Weekday(h.Date)} {h.Name}");
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>The long weekends that still remain this year for a country.</summary>
    /// <param name="country">ISO 3166 country code.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Each remaining long weekend, one per line, or a clean message.</returns>
    [KernelFunction("get_long_weekends")]
    [Description("List the long weekends remaining this year for a country, with their dates and day counts.")]
    public async Task<string> GetLongWeekendsAsync(
        [Description("ISO 3166 country code, e.g. GB or BR.")] string country = "GB",
        CancellationToken ct = default)
    {
        var code = Normalize(country);
        if (!IsValidCountryCode(code))
        {
            return CountryHint;
        }

        try
        {
            var today = RelaxedDate.Today(_clock, _timeZone);
            var weekends = await _client.GetLongWeekendsAsync(today.Year, code, ct).ConfigureAwait(false);
            var remaining = weekends.Where(w => w.EndDate >= today).OrderBy(w => w.StartDate).ToList();
            if (remaining.Count == 0)
            {
                return $"No remaining long weekends this year for {code}.";
            }

            var lines = remaining.Select(FormatLongWeekend);
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string FormatLongWeekend(LongWeekend w)
    {
        var dayLabel = w.DayCount == 1 ? "day" : "days";
        var bridge = w.NeedBridgeDay ? " (bridge day needed)" : string.Empty;
        return $"{Iso(w.StartDate)} to {Iso(w.EndDate)} ({w.DayCount} {dayLabel}){bridge}";
    }

    private string Normalize(string country)
    {
        var value = string.IsNullOrWhiteSpace(country) ? _defaultCountry : country;
        return value.Trim().ToUpperInvariant();
    }

    private static bool IsValidCountryCode(string code)
        => code.Length == 2 && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1]);

    private static bool TryParseYear(string year, out int parsed)
    {
        if (int.TryParse(year, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
            && parsed is >= 1900 and <= 2200)
        {
            return true;
        }

        parsed = 0;
        return false;
    }

    private static string DaysAway(DateOnly date, DateOnly today)
    {
        var days = date.DayNumber - today.DayNumber;
        return days switch
        {
            <= 0 => "today",
            1 => "in 1 day",
            _ => $"in {days} days",
        };
    }

    private static string Iso(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Weekday(DateOnly date)
        => date.DayOfWeek.ToString();

    private static bool IsHandledFailure(Exception ex)
        => ex is HolidaysApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        HolidaysApiException api => api.Message,
        OperationCanceledException => "The holidays request timed out. Try again later.",
        _ => "Could not reach the holidays service. Try again later.",
    };
}
