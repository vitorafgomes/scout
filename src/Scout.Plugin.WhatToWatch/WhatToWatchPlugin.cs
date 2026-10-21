using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.WhatToWatch.Abstractions;

namespace Scout.Plugin.WhatToWatch;

/// <summary>
/// Read-only TV plugin exposed to the LLM as Semantic Kernel functions. Search,
/// next-episode, and recommendation run against the free TVMaze API and always
/// work; streaming availability runs against TMDB and is available only when a
/// TMDB key is configured, degrading to a friendly hint otherwise. All data comes
/// from GET calls; no external state is ever changed. Every call returns a
/// non-empty, user-safe string; invalid input yields a corrective sentence and
/// transport or API failures are caught and surfaced as a clean error line, so a
/// tool call never throws back into the kernel.
/// </summary>
public sealed class WhatToWatchPlugin
{
    private const string TmdbKeyRequiredMessage =
        "Streaming lookup needs a TMDB key. Set Scout:Plugins:WhatToWatch:TmdbApiKey to enable where_to_watch; the other functions work without it.";

    private const string CountryHint = "Use ISO country codes like GB, BR, US.";

    private readonly ITvMazeClient _tvMaze;
    private readonly ITmdbClient? _tmdb;
    private readonly TimeProvider _clock;
    private readonly string _defaultCountry;
    private readonly string _timeZone;
    private readonly int _maxResults;

    /// <summary>Creates the plugin over TVMaze only, without streaming lookup.</summary>
    /// <param name="tvMaze">The TVMaze client. Must be non-null.</param>
    /// <param name="clock">Clock used to resolve "today". Must be non-null.</param>
    /// <param name="options">Plugin options. Must be non-null.</param>
    public WhatToWatchPlugin(ITvMazeClient tvMaze, TimeProvider clock, WhatToWatchOptions options)
        : this(tvMaze, tmdb: null, clock, options)
    {
    }

    /// <summary>
    /// Creates the plugin over TVMaze and an optional TMDB client. A null TMDB
    /// client disables <c>where_to_watch</c>, which then returns a friendly hint;
    /// the other functions still work.
    /// </summary>
    /// <param name="tvMaze">The TVMaze client. Must be non-null.</param>
    /// <param name="tmdb">The TMDB client, or null when no TMDB key is configured.</param>
    /// <param name="clock">Clock used to resolve "today". Must be non-null.</param>
    /// <param name="options">Plugin options. Must be non-null.</param>
    internal WhatToWatchPlugin(
        ITvMazeClient tvMaze,
        ITmdbClient? tmdb,
        TimeProvider clock,
        WhatToWatchOptions options)
    {
        _tvMaze = tvMaze ?? throw new ArgumentNullException(nameof(tvMaze));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _tmdb = tmdb;
        _defaultCountry = string.IsNullOrWhiteSpace(options.DefaultCountry) ? "GB" : options.DefaultCountry;
        _timeZone = string.IsNullOrWhiteSpace(options.TimeZone) ? TimeZoneInfo.Local.Id : options.TimeZone;
        _maxResults = options.MaxResults > 0 ? options.MaxResults : 5;
    }

    /// <summary>Search the show database for shows matching a title.</summary>
    /// <param name="title">The show title to search for.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Up to the configured number of matches, one per line, or a clean message.</returns>
    [KernelFunction("search_show")]
    [Description("Search TV shows by title, listing matches with their year, status, network, and genres.")]
    public async Task<string> SearchShowAsync(
        [Description("Show title to search, e.g. Breaking Bad or O Mecanismo.")] string title,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Provide a show title, e.g. Breaking Bad.";
        }

        try
        {
            var shows = await _tvMaze.SearchShowsAsync(title.Trim(), ct).ConfigureAwait(false);
            if (shows.Count == 0)
            {
                return $"No shows found matching '{title.Trim()}'.";
            }

            var lines = shows.Take(_maxResults).Select(FormatShow);
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>The next scheduled episode for a show.</summary>
    /// <param name="title">The show title.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The next episode with its air date, or a clean message.</returns>
    [KernelFunction("get_next_episode")]
    [Description("Find the next scheduled episode for a TV show, with its season, number, title, and air date.")]
    public async Task<string> GetNextEpisodeAsync(
        [Description("Show title, e.g. Severance or Round 6.")] string title,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Provide a show title, e.g. Severance.";
        }

        try
        {
            var result = await _tvMaze.GetNextEpisodeAsync(title.Trim(), ct).ConfigureAwait(false);
            if (result is null)
            {
                return $"No show found matching '{title.Trim()}' on TVMaze.";
            }

            if (result.NextEpisode is null)
            {
                return $"{result.ShowName} has no upcoming episode scheduled (it may be between seasons or ended).";
            }

            return FormatNextEpisode(result.ShowName, result.NextEpisode);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Recommend highly rated shows in a genre.</summary>
    /// <param name="genre">The genre to recommend within.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Up to the configured number of recommendations, one per line, or a clean message.</returns>
    [KernelFunction("recommend_shows")]
    [Description("Recommend highly rated TV shows in a given genre, best rated first.")]
    public async Task<string> RecommendShowsAsync(
        [Description("Genre to recommend, e.g. Drama, Comedy, or Science-Fiction.")] string genre,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return "Provide a genre, e.g. Drama.";
        }

        try
        {
            var shows = await _tvMaze.RecommendShowsAsync(genre.Trim(), ct).ConfigureAwait(false);
            if (shows.Count == 0)
            {
                return $"No {genre.Trim()} shows found to recommend.";
            }

            var lines = shows.Take(_maxResults).Select(FormatRecommendation);
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Where a show can be streamed, rented, or bought in a country.</summary>
    /// <param name="title">The show title.</param>
    /// <param name="country">ISO 3166 country code.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The providers by offer type, or a corrective, degrade, or clean message.</returns>
    [KernelFunction("where_to_watch")]
    [Description("Find where a TV show can be streamed, rented, or bought in a country. Requires a configured TMDB key.")]
    public async Task<string> WhereToWatchAsync(
        [Description("Show title, e.g. The Bear.")] string title,
        [Description("ISO 3166 country code, e.g. GB or BR.")] string country = "GB",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Provide a show title, e.g. The Bear.";
        }

        var code = NormalizeCountry(country);
        if (!IsValidCountryCode(code))
        {
            return CountryHint;
        }

        if (_tmdb is null)
        {
            return TmdbKeyRequiredMessage;
        }

        try
        {
            var availability = await _tmdb.GetWatchProvidersAsync(title.Trim(), code, ct).ConfigureAwait(false);
            if (availability is null)
            {
                return $"No title matching '{title.Trim()}' was found on TMDB.";
            }

            return FormatAvailability(availability);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string FormatShow(Show show)
    {
        var year = show.Year is { } y ? $" ({y.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        var network = show.Network is { Length: > 0 } n ? $" - {n}" : string.Empty;
        var genres = show.Genres.Count > 0 ? $" [{string.Join(", ", show.Genres)}]" : string.Empty;
        return $"{show.Name}{year} - {show.Status}{network}{genres}";
    }

    private static string FormatRecommendation(Show show)
    {
        var year = show.Year is { } y ? $" ({y.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        var rating = show.Rating is { } r
            ? $"rating {r.ToString("0.0", CultureInfo.InvariantCulture)}"
            : "rating n/a";
        var genres = show.Genres.Count > 0 ? $" [{string.Join(", ", show.Genres)}]" : string.Empty;
        return $"{show.Name}{year} - {rating}{genres}";
    }

    private string FormatNextEpisode(string showName, Episode episode)
    {
        var code = $"S{episode.Season.ToString("00", CultureInfo.InvariantCulture)}E{episode.Number.ToString("00", CultureInfo.InvariantCulture)}";
        if (episode.AirDate is not { } air)
        {
            return $"{showName} {code} - {episode.Name} (air date TBA).";
        }

        var today = RelaxedDate.Today(_clock, _timeZone);
        return $"{showName} {code} - {episode.Name} airs {Iso(air)} ({DaysAway(air, today)}).";
    }

    private static string FormatAvailability(WatchAvailability availability)
    {
        var lines = new List<string> { $"{availability.Title} in {availability.Country}:" };
        if (availability.Streaming.Count > 0)
        {
            lines.Add($"Stream: {string.Join(", ", availability.Streaming)}");
        }

        if (availability.Rent.Count > 0)
        {
            lines.Add($"Rent: {string.Join(", ", availability.Rent)}");
        }

        if (availability.Buy.Count > 0)
        {
            lines.Add($"Buy: {string.Join(", ", availability.Buy)}");
        }

        if (lines.Count == 1)
        {
            return $"No streaming providers are listed for '{availability.Title}' in {availability.Country}.";
        }

        return string.Join("\n", lines);
    }

    private string NormalizeCountry(string country)
    {
        var value = string.IsNullOrWhiteSpace(country) ? _defaultCountry : country;
        return value.Trim().ToUpperInvariant();
    }

    private static bool IsValidCountryCode(string code)
        => code.Length == 2 && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1]);

    private static string DaysAway(DateOnly date, DateOnly today)
    {
        var days = date.DayNumber - today.DayNumber;
        return days switch
        {
            < 0 => "already aired",
            0 => "today",
            1 => "in 1 day",
            _ => $"in {days.ToString(CultureInfo.InvariantCulture)} days",
        };
    }

    private static string Iso(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool IsHandledFailure(Exception ex)
        => ex is WhatToWatchApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        WhatToWatchApiException api => api.Message,
        OperationCanceledException => "The request to the show services timed out. Try again later.",
        _ => "Could not reach the show services. Try again later.",
    };
}
