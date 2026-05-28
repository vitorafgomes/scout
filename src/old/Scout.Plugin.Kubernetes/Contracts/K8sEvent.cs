namespace Scout.Plugin.Kubernetes.Contracts;

/// <summary>A flattened Kubernetes event entry.</summary>
public sealed record K8sEvent(
    string Namespace,
    string ObjectKind,
    string ObjectName,
    string Type,
    string Reason,
    string Message,
    int Count,
    DateTime LastSeen);
