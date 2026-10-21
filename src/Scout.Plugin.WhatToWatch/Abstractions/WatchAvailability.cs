namespace Scout.Plugin.WhatToWatch.Abstractions;

/// <summary>
/// Where a title can be watched in one country, split by offer type. Read-only
/// projection of a streaming-providers lookup.
/// </summary>
/// <param name="Title">The matched title, e.g. "The Bear".</param>
/// <param name="Country">The ISO 3166 country the availability applies to, e.g. "GB".</param>
/// <param name="Streaming">Subscription/streaming providers, e.g. ["Netflix", "Disney Plus"].</param>
/// <param name="Rent">Providers offering the title to rent.</param>
/// <param name="Buy">Providers offering the title to buy.</param>
public sealed record WatchAvailability(
    string Title,
    string Country,
    IReadOnlyList<string> Streaming,
    IReadOnlyList<string> Rent,
    IReadOnlyList<string> Buy);
