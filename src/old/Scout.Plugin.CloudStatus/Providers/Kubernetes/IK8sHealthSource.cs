namespace Scout.Plugin.CloudStatus.Providers.Kubernetes;

/// <summary>
/// Seam over the Kubernetes API calls. Lets <see cref="K8sCloudHealthProvider"/>
/// stay unit-testable without a real cluster, a running kubeconfig, or a
/// mocked <c>IKubernetes</c> sprawl.
/// </summary>
public interface IK8sHealthSource
{
    /// <summary>
    /// Return active health issues across nodes and pods. Empty list means
    /// the inspected cluster scope is fully healthy right now.
    /// Implementations propagate <see cref="OperationCanceledException"/>.
    /// Other exceptions bubble so the provider can map them.
    /// </summary>
    Task<IReadOnlyList<K8sHealthIssue>> GetActiveIssuesAsync(CancellationToken cancellationToken);
}
