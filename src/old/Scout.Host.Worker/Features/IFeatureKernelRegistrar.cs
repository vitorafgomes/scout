namespace Scout.Host.Worker.Features;

/// <summary>
/// Mutates the live Semantic Kernel plugin collection on feature enable
/// / disable. All calls are idempotent: register-when-already-registered
/// and unregister-unknown are no-ops.
/// </summary>
public interface IFeatureKernelRegistrar
{
    /// <summary>Attach the feature's plugin to the live kernel.</summary>
    Task RegisterAsync(string featureId, CancellationToken ct);

    /// <summary>Detach the feature's plugin from the live kernel.</summary>
    Task UnregisterAsync(string featureId, CancellationToken ct);

    /// <summary>True when the feature is currently attached.</summary>
    bool IsRegistered(string featureId);
}
