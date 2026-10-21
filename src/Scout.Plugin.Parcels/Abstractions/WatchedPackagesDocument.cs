namespace Scout.Plugin.Parcels.Abstractions;

/// <summary>
/// The persisted shape of the "watched-packages" collection in the Scout-local
/// store. Holds every package the user is watching. One document per machine.
/// </summary>
public sealed class WatchedPackagesDocument
{
    /// <summary>All watched packages, in the order they were added (oldest first).</summary>
    public List<WatchedPackage> Packages { get; set; } = [];
}
