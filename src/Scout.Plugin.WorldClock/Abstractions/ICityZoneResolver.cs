namespace Scout.Plugin.WorldClock.Abstractions;

/// <summary>
/// Resolves free-text city input to a <see cref="CityZone"/> against a bundled,
/// offline city-to-IANA table. Diacritic-insensitive and bilingual (accepts
/// Portuguese and English spellings). Purely local: never touches a network.
/// </summary>
public interface ICityZoneResolver
{
    /// <summary>
    /// Try to resolve <paramref name="query"/> (e.g. "London", "são paulo", or
    /// "Porto, Portugal") to a known city. Returns false when the city is unknown
    /// or when a bare name is ambiguous across countries and needs qualifying.
    /// </summary>
    bool TryResolve(string? query, out CityZone zone);
}
