using Scout.Core.Chat;

namespace Scout.Plugin.Kubernetes.Contracts;

/// <summary>Read-only snapshot of a single Kubernetes namespace.</summary>
public sealed record K8sNamespaceSummary(
    string Name,
    string Status,
    DateTime CreatedAt,
    Citation Citation);
