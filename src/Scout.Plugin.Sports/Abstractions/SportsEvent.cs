namespace Scout.Plugin.Sports.Abstractions;

/// <summary>
/// A single match, either finished or scheduled. Scores are null for fixtures that
/// have not been played. Minimal read-only projection.
/// </summary>
/// <param name="Name">Event label, e.g. "Arsenal vs Chelsea".</param>
/// <param name="Date">Kickoff date, when known.</param>
/// <param name="Time">Kickoff time as published (may be empty), e.g. "15:00:00".</param>
/// <param name="HomeTeam">Home team name.</param>
/// <param name="AwayTeam">Away team name.</param>
/// <param name="HomeScore">Home goals, null when the match has not been played.</param>
/// <param name="AwayScore">Away goals, null when the match has not been played.</param>
/// <param name="League">Competition name, e.g. "English Premier League".</param>
public sealed record SportsEvent(
    string Name,
    DateOnly? Date,
    string Time,
    string HomeTeam,
    string AwayTeam,
    int? HomeScore,
    int? AwayScore,
    string League);
