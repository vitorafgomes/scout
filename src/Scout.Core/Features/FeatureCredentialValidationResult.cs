namespace Scout.Core.Features;

/// <summary>
/// Result returned by <see cref="IFeatureCredentialValidator"/>.
/// <c>Metadata</c> lets plugins return supplementary context (kube
/// server version, available contexts, etc.) without forcing a generic
/// over the interface.
/// </summary>
public sealed record FeatureCredentialValidationResult(
    FeatureCredentialValidationOutcome Outcome,
    string? Detail = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
