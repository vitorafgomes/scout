namespace Scout.Plugin.Kubernetes.Contracts;

/// <summary>Per-container runtime status snapshot.</summary>
public sealed record K8sContainerStatus(
    string Name,
    string Image,
    string State,
    int Restarts,
    string? LastTerminationReason,
    int? ExitCode);
