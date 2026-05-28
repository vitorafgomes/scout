using Scout.Core.Chat;

namespace Scout.Plugin.Kubernetes.Contracts;

/// <summary>Read-only summary of a single Pod.</summary>
public sealed record K8sPodSummary(
    string Namespace,
    string Name,
    string Phase,
    int Restarts,
    int ContainerCount,
    int ReadyContainers,
    string Node,
    DateTime StartedAt,
    string? OwnerKind,
    string? OwnerName,
    Citation Citation);
