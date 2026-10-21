namespace Scout.Plugin.Sports.Abstractions;

/// <summary>
/// Read-only access to sports data (teams, matches, and league tables). Every
/// operation is an HTTP GET; no external state is ever mutated.
/// </summary>
public interface ISportsClient
{
    /// <summary>
    /// Search teams whose name matches <paramref name="name"/>. Returns an empty
    /// list when nothing matches.
    /// </summary>
    Task<IReadOnlyList<SportsTeam>> SearchTeamsAsync(string name, CancellationToken ct);

    /// <summary>
    /// The most recent matches for the team identified by <paramref name="teamId"/>,
    /// newest first as provided. Returns an empty list when none are available.
    /// </summary>
    Task<IReadOnlyList<SportsEvent>> GetLastEventsAsync(string teamId, CancellationToken ct);

    /// <summary>
    /// The upcoming fixtures for the team identified by <paramref name="teamId"/>.
    /// Returns an empty list when none are scheduled.
    /// </summary>
    Task<IReadOnlyList<SportsEvent>> GetNextEventsAsync(string teamId, CancellationToken ct);

    /// <summary>
    /// The catalogue of known leagues, used to resolve a fuzzy league name to an id.
    /// Returns an empty list when unavailable.
    /// </summary>
    Task<IReadOnlyList<SportsLeague>> ListLeaguesAsync(CancellationToken ct);

    /// <summary>
    /// The standings for <paramref name="leagueId"/> in <paramref name="season"/>
    /// (e.g. "2025-2026"); pass an empty season to let the provider pick the current
    /// one. Returns an empty list when no table is available.
    /// </summary>
    Task<IReadOnlyList<LeagueStanding>> GetLeagueTableAsync(
        string leagueId, string season, CancellationToken ct);
}
