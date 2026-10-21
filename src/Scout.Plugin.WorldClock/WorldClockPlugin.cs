using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.WorldClock.Abstractions;

namespace Scout.Plugin.WorldClock;

/// <summary>
/// DST-aware world-clock arithmetic exposed to the LLM as Semantic Kernel
/// functions. A small model must never reason about time zones or daylight saving
/// by hand, so each function resolves cities against a bundled offline city-to-IANA
/// table and computes offsets with <see cref="TimeZoneInfo"/> against an injected
/// <see cref="TimeProvider"/>: reproducible, never drifting. Read-only and local:
/// no network, no persistence, nothing external is touched. Every call returns a
/// non-empty, user-safe string; an unknown city yields a corrective hint and a tool
/// call never throws into the kernel.
/// </summary>
public sealed class WorldClockPlugin
{
    private const string TimeHint =
        "Give a time like 15:00, 9:30, or 3pm.";

    private static readonly string[] TimeFormats =
    [
        "H:mm",
        "HH:mm",
        "%H",
        "HH",
        "h:mmtt",
        "hh:mmtt",
        "htt",
        "hhtt",
        "H'h'mm",
        "HH'h'mm",
        "H'h'",
        "HH'h'",
    ];

    private readonly TimeProvider _clock;
    private readonly ICityZoneResolver _resolver;
    private readonly string _defaultTimeZone;

    /// <summary>Creates the plugin over a clock, a city resolver, and a default zone.</summary>
    /// <param name="clock">Time source. Must be non-null.</param>
    /// <param name="resolver">Offline city-to-IANA resolver. Must be non-null.</param>
    /// <param name="defaultTimeZone">IANA zone used when a call omits a city, e.g. "Europe/London".</param>
    public WorldClockPlugin(TimeProvider clock, ICityZoneResolver resolver, string defaultTimeZone)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultTimeZone);
        _defaultTimeZone = defaultTimeZone;
    }

    /// <summary>The current wall-clock time in a city, daylight-saving aware.</summary>
    /// <param name="city">City name; empty reports the configured home zone.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    /// <returns>A single sentence, a corrective hint on an unknown city, or a clean error line.</returns>
    [KernelFunction("current_time_in_city")]
    [Description("Get the current local time in a city, correct for daylight saving. Use this instead of guessing time zones.")]
    public Task<string> CurrentTimeInCityAsync(
        [Description("City name, e.g. \"Tokyo\" or \"Porto, Portugal\". Leave empty for the configured home zone.")] string city = "",
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            string ianaId;
            string? cityLabel;
            if (string.IsNullOrWhiteSpace(city))
            {
                ianaId = _defaultTimeZone;
                cityLabel = null;
            }
            else if (_resolver.TryResolve(city, out var resolved))
            {
                ianaId = resolved.IanaId;
                cityLabel = $"{resolved.City}, {resolved.Country}";
            }
            else
            {
                return Task.FromResult(UnknownCity(city));
            }

            if (!TryZone(ianaId, out var zone))
            {
                return Task.FromResult(ZoneUnavailable(cityLabel is null ? ianaId : $"{cityLabel} ({ianaId})"));
            }

            var now = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), zone);
            var date = DateOnly.FromDateTime(now.DateTime);
            var place = cityLabel is null
                ? $"{ianaId} (UTC{Offset(now.Offset)})"
                : $"{cityLabel} ({ianaId}, UTC{Offset(now.Offset)})";
            return Task.FromResult(
                $"It is {Hhmm(now)} on {DayName(date)} {Iso(date)} in {place}.");
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>Convert a wall-clock time from one city to another, daylight-saving aware.</summary>
    /// <param name="time">Time of day in the source city.</param>
    /// <param name="fromCity">Source city.</param>
    /// <param name="toCity">Target city.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    /// <returns>A single sentence, a corrective hint on bad input, or a clean error line.</returns>
    [KernelFunction("convert_time_between_cities")]
    [Description("Convert a clock time from one city to another, correct for daylight saving. Use this instead of guessing time zones.")]
    public Task<string> ConvertTimeBetweenCitiesAsync(
        [Description("Time of day in the source city, e.g. 15:00, 9:30, or 3pm.")] string time,
        [Description("Source city, e.g. \"São Paulo\".")] string fromCity,
        [Description("Target city, e.g. \"London\".")] string toCity,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (!TryParseTimeOfDay(time, out var parsedTime))
            {
                return Task.FromResult(TimeHint);
            }

            if (!_resolver.TryResolve(fromCity, out var from))
            {
                return Task.FromResult(UnknownCity(fromCity));
            }

            if (!_resolver.TryResolve(toCity, out var to))
            {
                return Task.FromResult(UnknownCity(toCity));
            }

            if (!TryZone(from.IanaId, out var fromZone))
            {
                return Task.FromResult(ZoneUnavailable($"{from.City}, {from.Country} ({from.IanaId})"));
            }

            if (!TryZone(to.IanaId, out var toZone))
            {
                return Task.FromResult(ZoneUnavailable($"{to.City}, {to.Country} ({to.IanaId})"));
            }

            var referenceDate = RelaxedDate.Today(_clock, from.IanaId);
            var naive = referenceDate.ToDateTime(parsedTime);
            var converted = TimeZoneInfo.ConvertTime(naive, fromZone, toZone);
            var shift = DateOnly.FromDateTime(converted).DayNumber - referenceDate.DayNumber;
            var suffix = shift switch
            {
                > 0 => " (next day)",
                < 0 => " (previous day)",
                _ => string.Empty,
            };

            return Task.FromResult(
                $"{Hhmm(parsedTime)} in {from.City}, {from.Country} ({from.IanaId}) is " +
                $"{Hhmm(TimeOnly.FromDateTime(converted))} in {to.City}, {to.Country} ({to.IanaId}){suffix}.");
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>The current time-zone difference between two cities, daylight-saving aware.</summary>
    /// <param name="fromCity">First city.</param>
    /// <param name="toCity">Second city.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    /// <returns>A single sentence, a corrective hint on an unknown city, or a clean error line.</returns>
    [KernelFunction("time_difference_between_cities")]
    [Description("Get the current time-zone difference between two cities, correct for daylight saving. Use this instead of guessing time zones.")]
    public Task<string> TimeDifferenceBetweenCitiesAsync(
        [Description("First city, e.g. \"London\".")] string fromCity,
        [Description("Second city, e.g. \"São Paulo\".")] string toCity,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (!_resolver.TryResolve(fromCity, out var from))
            {
                return Task.FromResult(UnknownCity(fromCity));
            }

            if (!_resolver.TryResolve(toCity, out var to))
            {
                return Task.FromResult(UnknownCity(toCity));
            }

            if (!TryZone(from.IanaId, out var fromZone))
            {
                return Task.FromResult(ZoneUnavailable($"{from.City}, {from.Country} ({from.IanaId})"));
            }

            if (!TryZone(to.IanaId, out var toZone))
            {
                return Task.FromResult(ZoneUnavailable($"{to.City}, {to.Country} ({to.IanaId})"));
            }

            var instant = _clock.GetUtcNow();
            var difference = fromZone.GetUtcOffset(instant) - toZone.GetUtcOffset(instant);
            var fromLabel = $"{from.City}, {from.Country}";
            var toLabel = $"{to.City}, {to.Country}";

            var text = difference == TimeSpan.Zero
                ? $"{fromLabel} and {toLabel} have the same time."
                : difference > TimeSpan.Zero
                    ? $"{fromLabel} is {SpanWord(difference)} ahead of {toLabel}."
                    : $"{fromLabel} is {SpanWord(difference.Duration())} behind {toLabel}.";
            return Task.FromResult(text);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    private static bool TryParseTimeOfDay(string input, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        foreach (var format in TimeFormats)
        {
            if (TimeOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryZone(string ianaId, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    private static string UnknownCity(string city) =>
        $"I could not find \"{city.Trim()}\". Use a city like \"London\", or a city and country like \"Porto, Portugal\".";

    private static string ZoneUnavailable(string label) =>
        $"Time zone data for {label} is unavailable on this system.";

    private static string Hhmm(DateTimeOffset when) => when.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Hhmm(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string DayName(DateOnly date) => date.DayOfWeek.ToString();

    private static string Offset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{sign}{Math.Abs(offset.Hours):D2}:{Math.Abs(offset.Minutes):D2}";
    }

    private static string SpanWord(TimeSpan span)
    {
        var hours = (int)span.TotalHours;
        var minutes = span.Minutes;
        var hourText = hours == 1 ? "1 hour" : $"{hours} hours";
        if (minutes == 0)
        {
            return hourText;
        }

        var minuteText = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        return hours == 0 ? minuteText : $"{hourText} {minuteText}";
    }

    // Local compute never crosses a network boundary; the only realistic throws
    // are cancellation and a wall-clock time pushed outside the calendar range.
    private static bool IsHandledFailure(Exception ex)
        => ex is OperationCanceledException or ArgumentOutOfRangeException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        OperationCanceledException => "The time lookup was cancelled.",
        ArgumentOutOfRangeException => "That time is outside the supported range.",
        _ => "Could not complete the time lookup.",
    };
}
