namespace Scout.Host.Worker.Features;

/// <summary>
/// Pinned validator contract shared by Phase 1, Phase 2 (GitHub) and
/// Phase 3 (Kubernetes). The credential body is a free dictionary so a
/// single endpoint dispatch covers all plugins; each validator picks
/// the keys it needs.
/// </summary>
public interface IFeatureCredentialValidator
{
    /// <summary>Feature id this validator belongs to.</summary>
    string FeatureId { get; }

    /// <summary>
    /// Validate the supplied credential body without persisting it.
    /// Implementations must not perform any external write.
    /// </summary>
    Task<FeatureCredentialValidationResult> ValidateAsync(
        IReadOnlyDictionary<string, string> credentialBody,
        CancellationToken cancellationToken);
}
