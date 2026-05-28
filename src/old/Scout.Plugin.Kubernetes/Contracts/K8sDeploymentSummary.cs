using Scout.Core.Chat;

namespace Scout.Plugin.Kubernetes.Contracts;

/// <summary>Read-only summary of a Deployment.</summary>
public sealed record K8sDeploymentSummary(
    string Namespace,
    string Name,
    int Replicas,
    int ReadyReplicas,
    int UpdatedReplicas,
    int AvailableReplicas,
    string? Strategy,
    Citation Citation);
