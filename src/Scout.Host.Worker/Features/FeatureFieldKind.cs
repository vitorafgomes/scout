namespace Scout.Host.Worker.Features;

/// <summary>
/// Input class of a credential field. Drives the renderer's form widget
/// (single-line vs multi-line) without leaking widget concepts into the
/// worker.
/// </summary>
public enum FeatureFieldKind
{
    /// <summary>Single-line opaque secret (e.g. a PAT).</summary>
    SingleLineSecret,

    /// <summary>Multi-line opaque secret (e.g. a kubeconfig).</summary>
    MultiLineSecret,
}
