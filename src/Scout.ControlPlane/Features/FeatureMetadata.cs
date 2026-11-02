namespace Scout.ControlPlane.Features;

/// <summary>
/// Per-feature write classification. A feature with any write row needs the
/// owner's grant before it is enabled.
/// </summary>
public enum WriteClass
{
    /// <summary>Read-only: no external mutations of any kind.</summary>
    None,

    /// <summary>
    /// Produces content a human or agent reads and acts on, such as drafts
    /// under Scout's own data folder; runs only under the owner's grant.
    /// </summary>
    Descriptive,

    /// <summary>
    /// Mutates external state; runs only under the owner's grant and, for
    /// irreversible rows, a per-action approval.
    /// </summary>
    Executive,
}

/// <summary>
/// Catalog entry for one Scout feature. The credential / settings / scope
/// lists are the single source of truth: both the runtime logic and the
/// UI-facing snapshot render from the same shapes.
/// </summary>
public sealed record FeatureMetadata(
    string Id,
    string DisplayName,
    string Description,
    bool RequiresCredentials,
    IReadOnlyList<FeatureCredentialField> CredentialFields,
    IReadOnlyList<FeatureSettingField> SettingsFields,
    WriteClass WriteClass,
    IReadOnlyList<FeatureScopeRow>? ScopeMatrix = null,
    IReadOnlyList<string>? SetupSteps = null)
{
    /// <summary>True when any scope row writes, reversibly or not.</summary>
    public bool HasWriteRows => ScopeMatrix?.Any(row => row.Access != ScopeAccess.Read) ?? false;
}
