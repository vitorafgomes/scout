namespace Scout.Plugin.Holidays.Abstractions;

/// <summary>
/// Read-only access to public-holiday data. Every operation is a GET; no external
/// state is ever mutated.
/// </summary>
public interface IHolidaysClient
{
    /// <summary>
    /// The upcoming public holidays for <paramref name="country"/> (an uppercase
    /// ISO 3166 code), soonest first.
    /// </summary>
    Task<IReadOnlyList<PublicHoliday>> GetNextHolidaysAsync(string country, CancellationToken ct);

    /// <summary>
    /// All public holidays in <paramref name="year"/> for <paramref name="country"/>
    /// (an uppercase ISO 3166 code).
    /// </summary>
    Task<IReadOnlyList<PublicHoliday>> GetHolidaysForYearAsync(int year, string country, CancellationToken ct);

    /// <summary>
    /// The long weekends in <paramref name="year"/> for <paramref name="country"/>
    /// (an uppercase ISO 3166 code).
    /// </summary>
    Task<IReadOnlyList<LongWeekend>> GetLongWeekendsAsync(int year, string country, CancellationToken ct);
}
