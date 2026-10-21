using k8s;

namespace Scout.Plugin.Kubernetes.Internal;

/// <summary>
/// Factory for the read-only <see cref="IKubernetes"/> client that
/// <see cref="KubernetesReadOps"/> consumes. The factory caches one
/// client per credential snapshot so the underlying connection pool
/// is shared across function calls.
/// </summary>
public interface IKubernetesClientFactory
{
    /// <summary>
    /// Returns a cached client or builds a new one if credentials have
    /// rotated. The caller does not own the returned instance and must
    /// not dispose it.
    /// </summary>
    Task<IKubernetes> GetClientAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached client. Called on credential rotation.</summary>
    void Invalidate();
}
