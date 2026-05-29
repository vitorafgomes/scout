namespace Scout.ControlPlane.Features;

/// <summary>
/// Activation surface a feature needs to be turned on/off at runtime.
/// In the worker this is backed by the Semantic Kernel plugin registrar;
/// in tests it is substituted. All calls are idempotent: register-when-
/// already-registered and unregister-unknown are no-ops.
/// </summary>
public interface IFeatureActivator
{
    /// <summary>True when a plugin implementation exists for this feature.</summary>
    bool CanActivate(string featureId);

    /// <summary>True when the feature is currently attached.</summary>
    bool IsActive(string featureId);

    /// <summary>Attach the feature's plugin to the live kernel.</summary>
    Task ActivateAsync(string featureId, CancellationToken ct);

    /// <summary>Detach the feature's plugin from the live kernel.</summary>
    Task DeactivateAsync(string featureId, CancellationToken ct);
}

/// <summary>
/// No-op activator: no plugins are wired. <see cref="CanActivate"/> always
/// returns false so enable returns <c>not-yet-implemented</c>. Used as the
/// default registration until SK plugins are layered on.
/// </summary>
public sealed class NullFeatureActivator : IFeatureActivator
{
    public bool CanActivate(string featureId) => false;

    public bool IsActive(string featureId) => false;

    public Task ActivateAsync(string featureId, CancellationToken ct) => Task.CompletedTask;

    public Task DeactivateAsync(string featureId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Raised when activation fails (plugin construction, kernel wiring).</summary>
public sealed class FeatureActivationException : Exception
{
    public FeatureActivationException(string message)
        : base(message)
    {
    }
}
