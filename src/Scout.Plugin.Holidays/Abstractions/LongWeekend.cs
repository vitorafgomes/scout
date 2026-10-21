namespace Scout.Plugin.Holidays.Abstractions;

/// <summary>
/// A long weekend: a contiguous stretch of non-working days built around a public
/// holiday. Read-only projection.
/// </summary>
/// <param name="StartDate">First day of the stretch.</param>
/// <param name="EndDate">Last day of the stretch.</param>
/// <param name="DayCount">Number of days off in the stretch.</param>
/// <param name="NeedBridgeDay">True when the stretch requires taking a bridge day off work.</param>
public sealed record LongWeekend(
    DateOnly StartDate,
    DateOnly EndDate,
    int DayCount,
    bool NeedBridgeDay);
