namespace Scout.Plugin.Sports.Abstractions;

/// <summary>
/// A single league returned by the league catalogue. Minimal read-only projection
/// used to resolve a fuzzy league name to a stable id before looking up a table.
/// </summary>
/// <param name="Id">Provider league id used for the standings lookup, e.g. "4328".</param>
/// <param name="Name">League name, e.g. "English Premier League".</param>
/// <param name="Sport">Sport the league belongs to, e.g. "Soccer".</param>
public sealed record SportsLeague(string Id, string Name, string Sport);
