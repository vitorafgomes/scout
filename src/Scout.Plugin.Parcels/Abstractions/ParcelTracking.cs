namespace Scout.Plugin.Parcels.Abstractions;

/// <summary>
/// A read-only snapshot of a parcel's tracking state. Produced by a lookup that
/// never registers or mutates anything on the provider.
/// </summary>
/// <param name="TrackingNumber">The carrier tracking number the snapshot is for.</param>
/// <param name="Status">The current status milestone, e.g. "in_transit" or "delivered".</param>
/// <param name="EstimatedDelivery">Estimated delivery date when known, otherwise null.</param>
/// <param name="Events">The known movements, newest first as returned by the provider.</param>
public sealed record ParcelTracking(
    string TrackingNumber,
    string Status,
    string? EstimatedDelivery,
    IReadOnlyList<TrackingEvent> Events);
