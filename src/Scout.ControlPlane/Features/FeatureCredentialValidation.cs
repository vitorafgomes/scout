namespace Scout.ControlPlane.Features;

/// <summary>
/// Superset of per-plugin validation outcomes. Each plugin may return
/// only a subset; the enum exists so the contract stays single.
/// </summary>
public enum FeatureCredentialValidationOutcome
{
    /// <summary>Credential body is valid and usable.</summary>
    Valid,

    /// <summary>Credential body parsed but the secret was rejected.</summary>
    Invalid,

    /// <summary>Credential authenticated but lacks the required scopes.</summary>
    MissingScopes,

    /// <summary>Authentication mechanism is not supported (e.g. exec plugin).</summary>
    UnsupportedAuth,

    /// <summary>Remote service was unreachable during validation.</summary>
    Unreachable,

    /// <summary>Remote service rate-limited the validation probe.</summary>
    RateLimited,

    /// <summary>Transient remote failure; the caller may retry.</summary>
    TransientError,

    /// <summary>Credential body itself is structurally malformed.</summary>
    Malformed,
}

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

/// <summary>
/// Validator contract: each feature picks the credential keys it needs.
/// The credential body is a free dictionary so a single dispatch covers all
/// features.
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

/// <summary>Raised by validators on a structurally invalid credential body.</summary>
public sealed class FeatureValidationException : Exception
{
    public FeatureValidationException(string message)
        : base(message)
    {
    }
}
