using System.Globalization;

namespace Scout.Plugin.Common;

/// <summary>
/// Tolerant date parsing shared by date-aware plugins (Weather, Holidays,
/// Dates, Reminders). Accepts ISO and common day/month formats plus a small set
/// of Portuguese and English relative words and weekday names, resolved against
/// an injected clock and time zone so behaviour is testable and never uses
/// <c>DateTime.Now</c>.
/// </summary>
public static class RelaxedDate
{
    private static readonly string[] AcceptedFormats =
    [
        "yyyy-MM-dd",
        "dd/MM/yyyy",
        "d/M/yyyy",
        "dd-MM-yyyy",
        "dd/MM",
        "d/M",
        "dd MMM",
        "dd MMMM",
    ];

    private static readonly IReadOnlyDictionary<string, int> RelativeDays =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["today"] = 0,
            ["hoje"] = 0,
            ["tomorrow"] = 1,
            ["amanha"] = 1,
            ["amanhã"] = 1,
            ["yesterday"] = -1,
            ["ontem"] = -1,
        };

    private static readonly IReadOnlyDictionary<string, DayOfWeek> Weekdays =
        new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["monday"] = DayOfWeek.Monday,
            ["segunda"] = DayOfWeek.Monday,
            ["segunda-feira"] = DayOfWeek.Monday,
            ["tuesday"] = DayOfWeek.Tuesday,
            ["terca"] = DayOfWeek.Tuesday,
            ["terça"] = DayOfWeek.Tuesday,
            ["terca-feira"] = DayOfWeek.Tuesday,
            ["terça-feira"] = DayOfWeek.Tuesday,
            ["wednesday"] = DayOfWeek.Wednesday,
            ["quarta"] = DayOfWeek.Wednesday,
            ["quarta-feira"] = DayOfWeek.Wednesday,
            ["thursday"] = DayOfWeek.Thursday,
            ["quinta"] = DayOfWeek.Thursday,
            ["quinta-feira"] = DayOfWeek.Thursday,
            ["friday"] = DayOfWeek.Friday,
            ["sexta"] = DayOfWeek.Friday,
            ["sexta-feira"] = DayOfWeek.Friday,
            ["saturday"] = DayOfWeek.Saturday,
            ["sabado"] = DayOfWeek.Saturday,
            ["sábado"] = DayOfWeek.Saturday,
            ["sunday"] = DayOfWeek.Sunday,
            ["domingo"] = DayOfWeek.Sunday,
        };

    /// <summary>The current instant expressed in <paramref name="timeZoneId"/> per the clock.</summary>
    public static DateTimeOffset Now(TimeProvider clock, string timeZoneId)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return TimeZoneInfo.ConvertTime(clock.GetUtcNow(), ResolveZone(timeZoneId));
    }

    /// <summary>The current date in <paramref name="timeZoneId"/> per the clock.</summary>
    public static DateOnly Today(TimeProvider clock, string timeZoneId)
        => DateOnly.FromDateTime(Now(clock, timeZoneId).DateTime);

    /// <summary>
    /// Try to interpret <paramref name="input"/> as a date. Handles ISO/day-month
    /// formats, relative words (today/tomorrow/yesterday, hoje/amanhã/ontem), and
    /// weekday names (next occurrence, en and pt). Two-field formats without a
    /// year assume the current year in the given zone.
    /// </summary>
    public static bool TryParse(
        string? input,
        TimeProvider clock,
        string timeZoneId,
        out DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(clock);
        date = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        var today = Today(clock, timeZoneId);

        if (RelativeDays.TryGetValue(text, out var offset))
        {
            date = today.AddDays(offset);
            return true;
        }

        if (Weekdays.TryGetValue(text, out var weekday))
        {
            date = NextWeekday(today, weekday);
            return true;
        }

        foreach (var format in AcceptedFormats)
        {
            if (DateTime.TryParseExact(
                    text,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                var year = format.Contains('y', StringComparison.Ordinal) ? parsed.Year : today.Year;
                date = new DateOnly(year, parsed.Month, parsed.Day);
                return true;
            }
        }

        return false;
    }

    /// <summary>A short sentence listing accepted date inputs, for corrective replies.</summary>
    public static string AcceptedFormatsHint() =>
        "Use a date like 2026-12-25 or 25/12/2026, or a word like today/tomorrow/friday (hoje/amanhã/sexta).";

    private static DateOnly NextWeekday(DateOnly from, DayOfWeek target)
    {
        var delta = ((int)target - (int)from.DayOfWeek + 7) % 7;
        if (delta == 0)
        {
            delta = 7;
        }

        return from.AddDays(delta);
    }

    private static TimeZoneInfo ResolveZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
