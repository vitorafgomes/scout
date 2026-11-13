namespace Scout.ControlPlane.Features;

/// <summary>
/// Read-only catalog of every Scout feature the worker knows about.
/// </summary>
public interface IFeatureCatalog
{
    /// <summary>All known features, in stable declaration order.</summary>
    IReadOnlyList<FeatureMetadata> All { get; }

    /// <summary>Lookup by id; returns null when unknown.</summary>
    FeatureMetadata? Find(string id);
}
