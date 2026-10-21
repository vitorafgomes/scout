using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.Sports.Abstractions;

namespace Scout.Plugin.Sports;

/// <summary>
/// Thin read-only client over TheSportsDB REST API using an injected,
/// pre-configured <see cref="HttpClient"/> (base address, timeout, and User-Agent
/// are set by DI; the API key lives in the base-address path segment). Non-success
/// responses and unreadable bodies become a user-safe <see cref="SportsApiException"/>;
/// timeouts and transport faults bubble unchanged. Every request is a GET; nothing
/// mutates remote state. "Not found" is signalled by the provider as a 200 with a
/// null array, which maps to an empty list rather than an error.
/// </summary>
internal sealed class SportsClient(HttpClient http) : ISportsClient
{
    private const string WireDateFormat = "yyyy-MM-dd";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<SportsTeam>> SearchTeamsAsync(string name, CancellationToken ct)
    {
        var dto = await GetAsync<SearchTeamsDto>(
            $"searchteams.php?t={Encode(name)}", ct).ConfigureAwait(false);
        return (dto.Teams ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Name))
            .Select(MapTeam)
            .ToList();
    }

    public async Task<IReadOnlyList<SportsEvent>> GetLastEventsAsync(string teamId, CancellationToken ct)
    {
        var dto = await GetAsync<LastEventsDto>(
            $"eventslast.php?id={Encode(teamId)}", ct).ConfigureAwait(false);
        return (dto.Results ?? []).Select(MapEvent).ToList();
    }

    public async Task<IReadOnlyList<SportsEvent>> GetNextEventsAsync(string teamId, CancellationToken ct)
    {
        var dto = await GetAsync<NextEventsDto>(
            $"eventsnext.php?id={Encode(teamId)}", ct).ConfigureAwait(false);
        return (dto.Events ?? []).Select(MapEvent).ToList();
    }

    public async Task<IReadOnlyList<SportsLeague>> ListLeaguesAsync(CancellationToken ct)
    {
        var dto = await GetAsync<AllLeaguesDto>("all_leagues.php", ct).ConfigureAwait(false);
        return (dto.Leagues ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l.Id) && !string.IsNullOrWhiteSpace(l.Name))
            .Select(MapLeague)
            .ToList();
    }

    public async Task<IReadOnlyList<LeagueStanding>> GetLeagueTableAsync(
        string leagueId, string season, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(season)
            ? $"lookuptable.php?l={Encode(leagueId)}"
            : $"lookuptable.php?l={Encode(leagueId)}&s={Encode(season)}";

        var dto = await GetAsync<TableDto>(path, ct).ConfigureAwait(false);
        return (dto.Table ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Team))
            .Select(MapStanding)
            .ToList();
    }

    private static SportsTeam MapTeam(TeamDto dto) => new(
        dto.Id ?? string.Empty,
        dto.Name ?? string.Empty,
        dto.League ?? string.Empty,
        dto.Country ?? string.Empty);

    private static SportsLeague MapLeague(LeagueDto dto) => new(
        dto.Id ?? string.Empty,
        dto.Name ?? string.Empty,
        string.IsNullOrWhiteSpace(dto.Sport) ? "Soccer" : dto.Sport);

    private static SportsEvent MapEvent(EventDto dto) => new(
        Name: dto.Name ?? string.Empty,
        Date: ParseDate(dto.Date),
        Time: dto.Time ?? string.Empty,
        HomeTeam: dto.HomeTeam ?? string.Empty,
        AwayTeam: dto.AwayTeam ?? string.Empty,
        HomeScore: ParseScore(dto.HomeScore),
        AwayScore: ParseScore(dto.AwayScore),
        League: dto.League ?? string.Empty);

    private static LeagueStanding MapStanding(StandingDto dto) => new(
        Rank: ParseInt(dto.Rank),
        Team: dto.Team ?? string.Empty,
        Played: ParseInt(dto.Played),
        Win: ParseInt(dto.Win),
        Draw: ParseInt(dto.Draw),
        Loss: ParseInt(dto.Loss),
        Points: ParseInt(dto.Points));

    private static string Encode(string value) => Uri.EscapeDataString(value ?? string.Empty);

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParseExact(
                value, WireDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static int? ParseScore(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score)
            ? score
            : null;

    private static int ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    /// <summary>
    /// GET that maps non-success responses and unreadable bodies to
    /// <see cref="SportsApiException"/>. Timeouts and transport faults bubble
    /// unchanged so the plugin can distinguish them.
    /// </summary>
    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new SportsApiException(
                "The sports service returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new SportsApiException(
                "The sports service returned an unreadable response.");
        }
    }

    private static SportsApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.NotFound:
                return new SportsApiException(
                    "No matching sports data was found.", status);
            case HttpStatusCode.TooManyRequests:
                return new SportsApiException(
                    "The sports service is rate limiting requests. Try again later.", status);
            default:
                if ((int)status >= 500)
                {
                    return new SportsApiException(
                        "The sports service is currently unavailable. Try again later.", status);
                }

                return new SportsApiException(
                    $"The sports request failed ({(int)status}).", status);
        }
    }

    private sealed record SearchTeamsDto(
        [property: JsonPropertyName("teams")] List<TeamDto>? Teams);

    private sealed record LastEventsDto(
        [property: JsonPropertyName("results")] List<EventDto>? Results);

    private sealed record NextEventsDto(
        [property: JsonPropertyName("events")] List<EventDto>? Events);

    private sealed record AllLeaguesDto(
        [property: JsonPropertyName("leagues")] List<LeagueDto>? Leagues);

    private sealed record TableDto(
        [property: JsonPropertyName("table")] List<StandingDto>? Table);

    private sealed record TeamDto(
        [property: JsonPropertyName("idTeam")] string? Id,
        [property: JsonPropertyName("strTeam")] string? Name,
        [property: JsonPropertyName("strLeague")] string? League,
        [property: JsonPropertyName("strCountry")] string? Country);

    private sealed record LeagueDto(
        [property: JsonPropertyName("idLeague")] string? Id,
        [property: JsonPropertyName("strLeague")] string? Name,
        [property: JsonPropertyName("strSport")] string? Sport);

    private sealed record EventDto(
        [property: JsonPropertyName("strEvent")] string? Name,
        [property: JsonPropertyName("dateEvent")] string? Date,
        [property: JsonPropertyName("strTime")] string? Time,
        [property: JsonPropertyName("strHomeTeam")] string? HomeTeam,
        [property: JsonPropertyName("strAwayTeam")] string? AwayTeam,
        [property: JsonPropertyName("intHomeScore")] string? HomeScore,
        [property: JsonPropertyName("intAwayScore")] string? AwayScore,
        [property: JsonPropertyName("strLeague")] string? League);

    private sealed record StandingDto(
        [property: JsonPropertyName("intRank")] string? Rank,
        [property: JsonPropertyName("strTeam")] string? Team,
        [property: JsonPropertyName("intPlayed")] string? Played,
        [property: JsonPropertyName("intWin")] string? Win,
        [property: JsonPropertyName("intDraw")] string? Draw,
        [property: JsonPropertyName("intLoss")] string? Loss,
        [property: JsonPropertyName("intPoints")] string? Points);
}
