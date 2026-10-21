using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;

namespace Scout.Plugin.Dates;

/// <summary>
/// Deterministic calendar arithmetic exposed to the LLM as Semantic Kernel
/// functions. A small model must never do date math by hand, so each function
/// computes the answer exactly against an injected <see cref="TimeProvider"/>:
/// reproducible, never drifting. Read-only and local: no network, no persistence,
/// nothing external is touched. Every call returns a non-empty, user-safe string;
/// bad input yields a corrective hint and a tool call never throws into the kernel.
/// </summary>
public sealed class DatesPlugin
{
    private readonly TimeProvider _clock;
    private readonly string _defaultTimeZone;

    /// <summary>Creates the plugin over an injected clock and a default IANA zone.</summary>
    /// <param name="clock">Time source. Must be non-null.</param>
    /// <param name="defaultTimeZone">IANA zone used when a call omits one, e.g. "Europe/London".</param>
    public DatesPlugin(TimeProvider clock, string defaultTimeZone)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultTimeZone);
        _defaultTimeZone = defaultTimeZone;
    }

    /// <summary>Today's date, weekday, and ISO week number.</summary>
    /// <param name="timezone">Optional IANA zone; empty uses the configured default.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    /// <returns>A single sentence, or a clean error line on failure.</returns>
    [KernelFunction("today")]
    [Description("Get today's date with its weekday and ISO week number. Use this instead of calculating dates yourself.")]
    public Task<string> TodayAsync(
        [Description("Optional IANA time zone, e.g. \"America/New_York\". Leave empty for the configured default.")] string timezone = "",
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var zone = string.IsNullOrWhiteSpace(timezone) ? _defaultTimeZone : timezone;
            var today = RelaxedDate.Today(_clock, zone);
            return Task.FromResult($"Today is {DayName(today)} {Iso(today)}, week {IsoWeek(today)}.");
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>The current local time of day, with date and time zone.</summary>
    /// <param name="timezone">Optional IANA zone; empty uses the configured default.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    /// <returns>A single sentence, or a clean error line on failure.</returns>
    [KernelFunction("current_time")]
    [Description("Get the current local time of day, with the date and time zone. Use this instead of guessing the time.")]
    public Task<string> CurrentTimeAsync(
        [Description("Optional IANA time zone, e.g. \"America/Sao_Paulo\". Leave empty for the configured default.")] string timezone = "",
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var zone = string.IsNullOrWhiteSpace(timezone) ? _defaultTimeZone : timezone;
            var now = RelaxedDate.Now(_clock, zone);
            var date = DateOnly.FromDateTime(now.DateTime);
            return Task.FromResult(
                $"It is {now.ToString("HH:mm", CultureInfo.InvariantCulture)} on {DayName(date)} {Iso(date)} in {zone} (UTC{Offset(now)}).");
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>Whole days between today and a target date.</summary>
    /// <param name="date">Target date to count to.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    /// <returns>A single sentence, a corrective hint on bad input, or a clean error line.</returns>
    [KernelFunction("days_until")]
    [Description("Count the whole days from today to a date (negative dates report how long ago). Use this instead of calculating dates yourself.")]
    public Task<string> DaysUntilAsync(
        [Description("Target date, e.g. 2026-12-25 (also accepts today/tomorrow or a weekday name).")] string date,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!RelaxedDate.TryParse(date, _clock, _defaultTimeZone, out var target))
            {
                return Task.FromResult(RelaxedDate.AcceptedFormatsHint());
            }

            var today = RelaxedDate.Today(_clock, _defaultTimeZone);
            var delta = target.DayNumber - today.DayNumber;

            var text = delta switch
            {
                > 0 => $"{DaysWord(delta)} until {Iso(target)} ({DayName(target)}).",
                < 0 => $"{DaysWord(-delta)} ago ({Iso(target)}, {DayName(target)}).",
                _ => $"That is today ({Iso(target)}, {DayName(target)}).",
            };
            return Task.FromResult(text);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>The weekday a given date falls on.</summary>
    /// <param name="date">Date to inspect.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    /// <returns>A single sentence, a corrective hint on bad input, or a clean error line.</returns>
    [KernelFunction("day_of_week")]
    [Description("Get the weekday a given date falls on. Use this instead of calculating dates yourself.")]
    public Task<string> DayOfWeekAsync(
        [Description("The date to inspect, e.g. 2026-09-15.")] string date,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!RelaxedDate.TryParse(date, _clock, _defaultTimeZone, out var parsed))
            {
                return Task.FromResult(RelaxedDate.AcceptedFormatsHint());
            }

            return Task.FromResult($"{Iso(parsed)} is a {DayName(parsed)}.");
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>The whole-day span between two dates, also expressed in weeks and days.</summary>
    /// <param name="startDate">First date.</param>
    /// <param name="endDate">Second date.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    /// <returns>A single sentence, a corrective hint on bad input, or a clean error line.</returns>
    [KernelFunction("date_difference")]
    [Description("Measure the number of days between two dates, also broken into weeks and days. Use this instead of calculating dates yourself.")]
    public Task<string> DateDifferenceAsync(
        [Description("Start date, e.g. 2026-07-03.")] string startDate,
        [Description("End date, e.g. 2026-08-26.")] string endDate,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!RelaxedDate.TryParse(startDate, _clock, _defaultTimeZone, out var start) ||
                !RelaxedDate.TryParse(endDate, _clock, _defaultTimeZone, out var end))
            {
                return Task.FromResult(RelaxedDate.AcceptedFormatsHint());
            }

            var total = Math.Abs(end.DayNumber - start.DayNumber);
            if (total < 7)
            {
                return Task.FromResult($"{DaysWord(total)}.");
            }

            var weeks = total / 7;
            var days = total % 7;
            var breakdown = days == 0 ? WeeksWord(weeks) : $"{WeeksWord(weeks)} and {DaysWord(days)}";
            return Task.FromResult($"{DaysWord(total)} ({breakdown}).");
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>Add a whole number of days to a date.</summary>
    /// <param name="date">Base date.</param>
    /// <param name="days">Days to add; may be negative.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    /// <returns>A single sentence, a corrective hint on bad input, or a clean error line.</returns>
    [KernelFunction("add_to_date")]
    [Description("Add (or subtract) a whole number of days to a date and get the resulting date and weekday. Use this instead of calculating dates yourself.")]
    public Task<string> AddToDateAsync(
        [Description("Base date, e.g. 2026-07-03.")] string date,
        [Description("Whole number of days to add; negative subtracts, e.g. 45 or -10.")] string days,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!RelaxedDate.TryParse(date, _clock, _defaultTimeZone, out var start))
            {
                return Task.FromResult(RelaxedDate.AcceptedFormatsHint());
            }

            if (!int.TryParse(
                    days?.Trim(),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var amount))
            {
                return Task.FromResult(
                    $"'{days}' is not a whole number of days. Use an integer like 45 or -10.");
            }

            var result = start.AddDays(amount);
            var op = amount < 0 ? "-" : "+";
            return Task.FromResult(
                $"{Iso(start)} {op} {DaysWord(Math.Abs(amount))} = {DayName(result)} {Iso(result)}.");
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string DayName(DateOnly date) => date.DayOfWeek.ToString();

    private static int IsoWeek(DateOnly date) => ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue));

    private static string Offset(DateTimeOffset when)
    {
        var offset = when.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{sign}{Math.Abs(offset.Hours):D2}:{Math.Abs(offset.Minutes):D2}";
    }

    private static string DaysWord(int count) => count == 1 ? "1 day" : $"{count} days";

    private static string WeeksWord(int count) => count == 1 ? "1 week" : $"{count} weeks";

    // Local compute never crosses a network boundary; the only realistic throws
    // are cancellation and a date pushed outside the supported calendar range.
    private static bool IsHandledFailure(Exception ex)
        => ex is OperationCanceledException or ArgumentOutOfRangeException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        OperationCanceledException => "Date calculation was cancelled.",
        ArgumentOutOfRangeException => "That date is outside the supported range.",
        _ => "Could not complete the date calculation.",
    };
}
