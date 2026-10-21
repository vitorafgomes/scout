namespace Scout.Plugin.Sports.Abstractions;

/// <summary>
/// A single team returned by a team search. Minimal read-only projection used to
/// resolve a fuzzy user name to a stable id before looking up results or fixtures.
/// </summary>
/// <param name="Id">Provider team id used for follow-up lookups, e.g. "133604".</param>
/// <param name="Name">Team name, e.g. "Arsenal".</param>
/// <param name="League">Primary league the team plays in, e.g. "English Premier League".</param>
/// <param name="Country">Country the team is based in, e.g. "England".</param>
public sealed record SportsTeam(string Id, string Name, string League, string Country);
