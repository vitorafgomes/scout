namespace Scout.Plugin.Currency.Abstractions;

/// <summary>
/// A summary of an exchange rate over a window. Aggregated from the ECB daily
/// series; all values are units of <paramref name="Target"/> per one unit of
/// <paramref name="Base"/>.
/// </summary>
/// <param name="Base">Source currency ISO code, e.g. "GBP".</param>
/// <param name="Target">Target currency ISO code, e.g. "BRL".</param>
/// <param name="Low">Lowest rate observed in the window.</param>
/// <param name="High">Highest rate observed in the window.</param>
/// <param name="First">Rate on the earliest observed date.</param>
/// <param name="Last">Rate on the latest observed date.</param>
/// <param name="StartDate">Earliest date with an observation.</param>
/// <param name="EndDate">Latest date with an observation.</param>
public sealed record RateTrend(
    string Base,
    string Target,
    decimal Low,
    decimal High,
    decimal First,
    decimal Last,
    DateOnly StartDate,
    DateOnly EndDate);
