namespace Scout.Plugin.CloudStatus.Providers.Kubernetes;

/// <summary>
/// One actionable health finding from the cluster. The provider collapses a
/// list of these into a <see cref="Domain.StatusReport"/>.
/// </summary>
/// <param name="Kind">
/// Kubernetes kind — <c>Node</c> or <c>Pod</c>. Used as the
/// <see cref="Domain.ServiceStatus.Service"/> label so the chat line shows
/// which resource type is impacted.
/// </param>
/// <param name="Name">Resource name, e.g. <c>ip-10-0-1-23.eu-west-1.compute.internal</c>.</param>
/// <param name="Namespace">Namespace for pods; <c>(cluster)</c> for cluster-scoped resources like nodes.</param>
/// <param name="Reason">
/// Machine-readable reason — e.g. <c>NodeNotReady</c>, <c>CrashLoopBackOff</c>,
/// <c>ImagePullBackOff</c>, <c>Pending</c>.
/// </param>
/// <param name="Severity">
/// Relative severity. Values: <c>critical</c> (Node NotReady,
/// CrashLoopBackOff, ImagePullBackOff, Error), <c>warning</c> (Pending,
/// Unknown). Matching is case-insensitive.
/// </param>
public sealed record K8sHealthIssue(
    string Kind,
    string Name,
    string Namespace,
    string Reason,
    string Severity);
