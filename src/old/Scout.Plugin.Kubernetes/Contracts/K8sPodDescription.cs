using Scout.Core.Chat;

namespace Scout.Plugin.Kubernetes.Contracts;

/// <summary>Composite read-only view of a Pod plus its recent events.</summary>
public sealed record K8sPodDescription(
    K8sPodSummary Summary,
    IReadOnlyList<K8sContainerStatus> Containers,
    IReadOnlyList<K8sEvent> RecentEvents,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Annotations,
    Citation Citation);
