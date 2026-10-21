namespace Scout.Plugin.Holidays.Abstractions;

/// <summary>
/// A single public holiday for a country. Minimal read-only projection.
/// </summary>
/// <param name="Date">The calendar date of the holiday.</param>
/// <param name="LocalName">The holiday name in the country's language, e.g. "Ano Novo".</param>
/// <param name="Name">The English holiday name, e.g. "New Year's Day".</param>
public sealed record PublicHoliday(DateOnly Date, string LocalName, string Name);
