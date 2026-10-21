namespace Scout.Plugin.Sports.Abstractions;

/// <summary>
/// A single row of a league table. Minimal read-only projection.
/// </summary>
/// <param name="Rank">Position in the table, 1-based.</param>
/// <param name="Team">Team name, e.g. "Arsenal".</param>
/// <param name="Played">Games played.</param>
/// <param name="Win">Games won.</param>
/// <param name="Draw">Games drawn.</param>
/// <param name="Loss">Games lost.</param>
/// <param name="Points">Total points.</param>
public sealed record LeagueStanding(
    int Rank,
    string Team,
    int Played,
    int Win,
    int Draw,
    int Loss,
    int Points);
