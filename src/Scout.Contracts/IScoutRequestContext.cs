namespace Scout.Contracts;

/// <summary>
/// Ambient accessor for the current turn's <see cref="ScoutRequestIdentity"/>.
/// A single shared singleton whose <see cref="Current"/> value is scoped per
/// logical async flow, so it works with Scout's singleton kernel/plugin/gateway
/// wiring: the conductor opens a scope at the choke point and every function the
/// kernel invokes while the response streams observes the same identity.
///
/// This is deliberately not a Semantic Kernel function parameter (the direct-chat
/// path exposes none, and a model-visible identity argument could be forged) and
/// not a scoped DI service (the kernel, plugins, and gateway are all singletons
/// resolved once).
/// </summary>
public interface IScoutRequestContext
{
    /// <summary>
    /// The identity for the current async flow, or <c>null</c> when no scope is
    /// active. Consumers must treat <c>null</c> as the least-privileged caller.
    /// </summary>
    ScoutRequestIdentity? Current { get; }

    /// <summary>
    /// Set <paramref name="identity"/> as <see cref="Current"/> for the current
    /// async flow until the returned handle is disposed, at which point the
    /// previous value is restored. Nesting-safe: scopes restore in stack order.
    /// </summary>
    /// <param name="identity">The identity to make current. Never null.</param>
    /// <returns>A handle that restores the previous identity when disposed.</returns>
    IDisposable BeginScope(ScoutRequestIdentity identity);
}
