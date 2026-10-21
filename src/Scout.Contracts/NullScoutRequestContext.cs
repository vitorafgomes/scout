namespace Scout.Contracts;

/// <summary>
/// No-op <see cref="IScoutRequestContext"/> whose <see cref="Current"/> is always
/// <c>null</c>. For hosts and tests that never set an identity: consumers then see
/// the fail-closed, least-privileged path. <see cref="BeginScope"/> is accepted
/// but has no effect, so callers can wire the same code path unconditionally.
/// </summary>
public sealed class NullScoutRequestContext : IScoutRequestContext
{
    /// <summary>Shared stateless instance.</summary>
    public static NullScoutRequestContext Instance { get; } = new();

    /// <inheritdoc />
    public ScoutRequestIdentity? Current => null;

    /// <inheritdoc />
    public IDisposable BeginScope(ScoutRequestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return NoopScope.Instance;
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
            // Intentionally empty: this context holds no identity.
        }
    }
}
