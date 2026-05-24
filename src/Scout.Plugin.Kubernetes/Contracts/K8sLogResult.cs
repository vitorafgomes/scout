using Scout.Core.Chat;

namespace Scout.Plugin.Kubernetes.Contracts;

/// <summary>Returned by <c>get_pod_logs</c>. <see cref="Truncated"/> is true when the tail length was clamped.</summary>
public sealed record K8sLogResult(
    string Namespace,
    string Pod,
    string Container,
    string Logs,
    bool Truncated,
    int? TotalLines,
    DateTime FetchedAt,
    Citation Citation);
