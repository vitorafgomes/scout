namespace Scout.Host.Worker.Features;

/// <summary>
/// Superset of per-plugin validation outcomes. Each plugin may return
/// only a subset; the enum exists so the contract stays single across
/// Phases 1-3.
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
