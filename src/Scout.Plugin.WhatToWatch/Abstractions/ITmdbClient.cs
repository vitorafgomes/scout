namespace Scout.Plugin.WhatToWatch.Abstractions;

/// <summary>
/// Read-only access to TMDB watch-provider data. Every operation is a GET; no
/// external state is ever mutated. Registered only when a TMDB API key is
/// configured, so the plugin holds this dependency as nullable and degrades
/// gracefully when it is absent.
/// </summary>
public interface ITmdbClient
{
    /// <summary>
    /// Where the show best matching <paramref name="title"/> can be watched in
    /// <paramref name="country"/> (an uppercase ISO 3166 code). Returns null when
    /// no title matches.
    /// </summary>
    Task<WatchAvailability?> GetWatchProvidersAsync(string title, string country, CancellationToken ct);
}
