namespace Scout.Plugin.WhatToWatch.Abstractions;

/// <summary>
/// A television show as returned by the show database. Minimal read-only
/// projection used for search and recommendation results.
/// </summary>
/// <param name="Name">The show's title, e.g. "Breaking Bad".</param>
/// <param name="Year">The premiere year when known, e.g. 2008; null when unknown.</param>
/// <param name="Network">The airing network or streaming channel, e.g. "AMC"; null when unknown.</param>
/// <param name="Status">The production status, e.g. "Running" or "Ended".</param>
/// <param name="Genres">The show's genres, e.g. ["Drama", "Crime"].</param>
/// <param name="Rating">The average viewer rating out of 10 when known; null otherwise.</param>
public sealed record Show(
    string Name,
    int? Year,
    string? Network,
    string Status,
    IReadOnlyList<string> Genres,
    double? Rating);
