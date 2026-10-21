namespace Scout.Plugin.Parcels.Abstractions;

/// <summary>
/// Read-only access to parcel tracking data. The lookup is a tracking search that
/// does not create or subscribe a tracker on the provider, so no external state
/// is ever mutated.
/// </summary>
public interface IParcelsClient
{
    /// <summary>
    /// Look up the current tracking state for <paramref name="trackingNumber"/>.
    /// Returns null when the provider has no information for the number (unknown
    /// or not yet in the carrier network).
    /// </summary>
    Task<ParcelTracking?> TrackAsync(string trackingNumber, CancellationToken ct);
}
