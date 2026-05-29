namespace Scout.ControlPlane.Features;

/// <summary>
/// Per-feature write classification under the descriptive/executive split.
/// </summary>
public enum WriteClass
{
    /// <summary>Read-only — no external mutations of any kind.</summary>
    None,

    /// <summary>
    /// Mutations that produce content for a human/agent to read and act
    /// on (e.g. opening a GitHub issue). Permitted.
    /// </summary>
    Descriptive,

    /// <summary>
    /// Mutations that directly change infra/code/data without a human
    /// gate. Forbidden by the Scout security invariant.
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
    IReadOnlyList<string>? SetupSteps = null);
