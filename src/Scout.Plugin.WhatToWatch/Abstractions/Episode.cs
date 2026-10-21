namespace Scout.Plugin.WhatToWatch.Abstractions;

/// <summary>
/// A single television episode. Minimal read-only projection.
/// </summary>
/// <param name="Season">The season number, e.g. 2.</param>
/// <param name="Number">The episode number within the season, e.g. 3.</param>
/// <param name="Name">The episode title, e.g. "The Suitcase".</param>
/// <param name="AirDate">The scheduled air date when known; null when the date is yet to be announced.</param>
public sealed record Episode(
    int Season,
    int Number,
    string Name,
    DateOnly? AirDate);
