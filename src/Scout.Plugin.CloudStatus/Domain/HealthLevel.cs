namespace Scout.Plugin.CloudStatus.Domain;

/// <summary>
/// Coarse-grained health level returned by a cloud health provider. Ordered
/// so an aggregate can be computed by taking the maximum across services.
/// </summary>
public enum HealthLevel
{
    /// <summary>Status is not known (auth failure, provider unreachable, etc.).</summary>
    Unknown = 0,

    /// <summary>All services operating normally.</summary>
    Healthy = 1,

    /// <summary>Partial impact — elevated errors, slow responses, etc.</summary>
    Degraded = 2,

    /// <summary>Full outage of at least one critical service in scope.</summary>
    Unhealthy = 3,
}
