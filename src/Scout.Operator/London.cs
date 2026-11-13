using System.Globalization;

namespace Scout.Operator;

/// <summary>
/// The owner's clock: Europe/London, else the Windows <c>GMT Standard Time</c>, else UTC on a host without time zone data,
/// found without ever throwing.
/// </summary>
internal static class London
{
    internal static readonly TimeZoneInfo Zone = Find(TimeZoneInfo.FindSystemTimeZoneById);

    /// <summary>The London time zone through <paramref name="find"/>, or UTC when it knows neither name.</summary>
    internal static TimeZoneInfo Find(Func<string, TimeZoneInfo> find)
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            try
            {
                return find(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    /// <summary><paramref name="at"/> on the London clock.</summary>
    internal static DateTimeOffset Of(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone);

    /// <summary><paramref name="at"/> as the owner reads it, for example "Tuesday 10 November 2026 17:00 London time".</summary>
    internal static string Format(DateTimeOffset at) =>
        Of(at).ToString("dddd d MMMM yyyy HH:mm", CultureInfo.InvariantCulture) + " London time";
}
