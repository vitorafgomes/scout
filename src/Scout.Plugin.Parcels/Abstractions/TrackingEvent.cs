namespace Scout.Plugin.Parcels.Abstractions;

/// <summary>
/// A single movement in a parcel's journey. Minimal read-only projection of the
/// provider's tracking event.
/// </summary>
/// <param name="OccurredAt">When the event happened, as reported by the carrier, e.g. "2026-01-02T10:00:00".</param>
/// <param name="Status">Human-readable event description, e.g. "Departed facility".</param>
/// <param name="Location">Where the event happened, e.g. "London, GB". Null when the carrier omits it.</param>
public sealed record TrackingEvent(string OccurredAt, string Status, string? Location);
