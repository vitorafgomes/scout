namespace Scout.Host.Worker.Features;

/// <summary>
/// Read-only catalog of every Scout feature the worker knows about.
/// Phase 1 ships two entries (<c>github</c> and <c>kubernetes</c>) so the
/// UI can render placeholder cards before the plugins are implemented.
/// </summary>
public interface IFeatureCatalog
{
    /// <summary>All known features, in stable declaration order.</summary>
    IReadOnlyList<FeatureMetadata> All { get; }

    /// <summary>Lookup by id; returns null when unknown.</summary>
    FeatureMetadata? Find(string id);
}
