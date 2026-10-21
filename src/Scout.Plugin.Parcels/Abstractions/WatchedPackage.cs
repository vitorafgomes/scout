namespace Scout.Plugin.Parcels.Abstractions;

/// <summary>A single parcel the user has added to the Scout-local watch list.</summary>
public sealed class WatchedPackage
{
    /// <summary>The carrier tracking number, normalized to upper case.</summary>
    public string TrackingNumber { get; set; } = string.Empty;

    /// <summary>Optional friendly label the user attached, e.g. "Nike order". Empty when none.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>When the package was added to the watch list, in UTC, from the injected clock.</summary>
    public DateTimeOffset AddedUtc { get; set; }
}
