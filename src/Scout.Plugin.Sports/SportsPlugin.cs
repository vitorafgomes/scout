using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Sports.Abstractions;

namespace Scout.Plugin.Sports;

/// <summary>
/// Read-only sports plugin exposed to the LLM as Semantic Kernel functions. Sources
/// football results, fixtures, and league tables from TheSportsDB. Every call is a
/// GET; no external state is ever changed. Each method returns a non-empty,
/// user-safe string; a fuzzy or ambiguous team/league yields a corrective
/// "Did you mean ...?" line so the model can self-repair, and transport/API
/// failures are caught and surfaced as a clean error line, so a tool call never
/// throws back into the kernel.
/// </summary>
public sealed class SportsPlugin
{
    private const string InvalidTeamMessage = "Provide a team name like Flamengo or Arsenal.";

    private const string InvalidLeagueMessage =
        "Provide a league name like Premier League or Brasileirao Serie A.";

    private const string NoLeagueHint =
        "No matching league found. Try Premier League, La Liga, Serie A, Bundesliga, or Brasileirao.";

    private readonly ISportsClient _client;
    private readonly TimeProvider _clock;
    private readonly string _timeZone;

    /// <summary>Creates the plugin over a read-only sports client and a clock.</summary>
    /// <param name="client">The sports client. Must be non-null.</param>
    /// <param name="clock">Clock used to resolve the current season. Must be non-null.</param>
    /// <param name="options">Plugin options supplying the time zone. Must be non-null.</param>
    public SportsPlugin(ISportsClient client, TimeProvider clock, SportsOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _timeZone = string.IsNullOrWhiteSpace(options.TimeZone) ? "Europe/London" : options.TimeZone;
    }

    /// <summary>A team's most recent finished match result.</summary>
    /// <param name="team">Team name to look up.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>A one-line score summary, a corrective line, or a clean message.</returns>
    [KernelFunction("get_last_result")]
    [Description("Get a football team's most recent finished match result, with the score, competition, and date.")]
    public async Task<string> GetLastResultAsync(
        [Description("Team name, e.g. Arsenal or Flamengo.")] string team,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(team))
        {
            return InvalidTeamMessage;
        }

        try
        {
            var (resolved, correction) = await ResolveTeamAsync(team, ct).ConfigureAwait(false);
            if (resolved is null)
            {
                return correction!;
            }

            var events = await _client.GetLastEventsAsync(resolved.Id, ct).ConfigureAwait(false);
            var finished = events
                .Where(e => e.HomeScore is not null && e.AwayScore is not null)
                .OrderByDescending(e => e.Date ?? DateOnly.MinValue)
                .ToList();
            if (finished.Count == 0)
            {
                return $"No recent result found for {resolved.Name}.";
            }

            return FormatResult(finished[0]);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>A team's next scheduled fixture.</summary>
    /// <param name="team">Team name to look up.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>A one-line fixture summary, a corrective line, or a clean message.</returns>
    [KernelFunction("get_next_fixture")]
    [Description("Get a football team's next scheduled fixture, with the opponent, competition, date, and kickoff time.")]
    public async Task<string> GetNextFixtureAsync(
        [Description("Team name, e.g. Arsenal or Flamengo.")] string team,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(team))
        {
            return InvalidTeamMessage;
        }

        try
        {
            var (resolved, correction) = await ResolveTeamAsync(team, ct).ConfigureAwait(false);
            if (resolved is null)
            {
                return correction!;
            }

            var events = await _client.GetNextEventsAsync(resolved.Id, ct).ConfigureAwait(false);
            var upcoming = events
                .OrderBy(e => e.Date ?? DateOnly.MaxValue)
                .ToList();
            if (upcoming.Count == 0)
            {
                return $"No upcoming fixture found for {resolved.Name}.";
            }

            return FormatFixture(upcoming[0]);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>The current standings for a league.</summary>
    /// <param name="league">League name to look up.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The table, one row per line, a corrective line, or a clean message.</returns>
    [KernelFunction("get_league_table")]
    [Description("Show the current standings for a football league: rank, team, points, and games played, one row per line.")]
    public async Task<string> GetLeagueTableAsync(
        [Description("League name, e.g. Premier League or Brasileirao Serie A.")] string league,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            return InvalidLeagueMessage;
        }

        try
        {
            var leagues = await _client.ListLeaguesAsync(ct).ConfigureAwait(false);
            var (resolved, correction) = ResolveLeague(leagues, league);
            if (resolved is null)
            {
                return correction!;
            }

            var season = CurrentSeason();
            var standings = await _client
                .GetLeagueTableAsync(resolved.Id, season, ct).ConfigureAwait(false);
            if (standings.Count == 0)
            {
                return $"No current league table available for {resolved.Name}.";
            }

            var lines = standings.OrderBy(s => s.Rank).Select(FormatStanding);
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private async Task<(SportsTeam? Team, string? Correction)> ResolveTeamAsync(
        string name, CancellationToken ct)
    {
        var query = name.Trim();
        var teams = await _client.SearchTeamsAsync(query, ct).ConfigureAwait(false);
        if (teams.Count == 0)
        {
            return (null, $"No team found matching '{query}'. Try a full club name like Flamengo or Arsenal.");
        }

        var exact = teams.FirstOrDefault(
            t => string.Equals(t.Name, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return (exact, null);
        }

        if (teams.Count == 1)
        {
            return (teams[0], null);
        }

        var suggestions = string.Join(", ", teams
            .Take(5)
            .Select(t => string.IsNullOrWhiteSpace(t.Country) ? t.Name : $"{t.Name} ({t.Country})"));
        return (null, $"Did you mean: {suggestions}?");
    }

    private static (SportsLeague? League, string? Correction) ResolveLeague(
        IReadOnlyList<SportsLeague> leagues, string name)
    {
        var query = name.Trim();

        var exact = leagues.FirstOrDefault(
            l => string.Equals(l.Name, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return (exact, null);
        }

        var contains = leagues
            .Where(l => l.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (contains.Count == 1)
        {
            return (contains[0], null);
        }

        var candidates = contains.Count > 0 ? contains : FuzzyLeagues(leagues, query);
        if (candidates.Count == 0)
        {
            return (null, NoLeagueHint);
        }

        var suggestions = string.Join(", ", candidates
            .Take(5)
            .Select(l => $"{l.Name} ({l.Sport})"));
        return (null, $"Did you mean: {suggestions}?");
    }

    private static IReadOnlyList<SportsLeague> FuzzyLeagues(
        IReadOnlyList<SportsLeague> leagues, string query)
    {
        var words = query.Split(
            [' ', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }

        return leagues
            .Where(l => words.Any(
                w => l.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private string CurrentSeason()
    {
        // European football seasons straddle two calendar years and begin in
        // August; before then the current season carries the previous year.
        var today = RelaxedDate.Today(_clock, _timeZone);
        return today.Month >= 8
            ? $"{today.Year}-{today.Year + 1}"
            : $"{today.Year - 1}-{today.Year}";
    }

    // A finished result is dated but the kickoff time adds no value; a fixture
    // keeps the time so the reader knows when to tune in.
    private static string FormatResult(SportsEvent e)
        => $"{e.HomeTeam} {e.HomeScore}-{e.AwayScore} {e.AwayTeam} ({Context(e, includeTime: false)})";

    private static string FormatFixture(SportsEvent e)
        => $"{e.HomeTeam} vs {e.AwayTeam} - {Context(e, includeTime: true)}";

    private static string Context(SportsEvent e, bool includeTime)
    {
        var league = string.IsNullOrWhiteSpace(e.League) ? "unknown competition" : e.League;
        var when = FormatWhen(e.Date, includeTime ? e.Time : string.Empty);
        return string.IsNullOrEmpty(when) ? league : $"{league}, {when}";
    }

    private static string FormatWhen(DateOnly? date, string time)
    {
        if (date is null)
        {
            return string.Empty;
        }

        var iso = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var kickoff = ShortTime(time);
        return string.IsNullOrEmpty(kickoff) ? iso : $"{iso} {kickoff}";
    }

    private static string ShortTime(string time)
    {
        if (string.IsNullOrWhiteSpace(time))
        {
            return string.Empty;
        }

        // Provider times arrive as "HH:mm:ss"; trim to "HH:mm" for readability.
        var trimmed = time.Trim();
        var parts = trimmed.Split(':');
        return parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : trimmed;
    }

    private static string FormatStanding(LeagueStanding s)
        => $"{s.Rank}. {s.Team} - {s.Points} pts ({s.Played} GP, {s.Win}W {s.Draw}D {s.Loss}L)";

    private static bool IsHandledFailure(Exception ex)
        => ex is SportsApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        SportsApiException api => api.Message,
        OperationCanceledException => "The sports request timed out. Try again later.",
        _ => "Could not reach the sports service. Try again later.",
    };
}
