namespace Scout.Contracts;

/// <summary>
/// <see cref="AsyncLocal{T}"/>-backed <see cref="IScoutRequestContext"/>. The
/// value flows across <c>await</c>, <c>await foreach</c>, and streaming
/// <see cref="IAsyncEnumerable{T}"/> because it rides the ExecutionContext, which
/// is captured on every asynchronous continuation. It is therefore NOT dropped by
/// <c>ConfigureAwait(false)</c> (that suppresses only the SynchronizationContext
/// capture, never the ExecutionContext), so identity set at the conductor choke
/// point is visible to Semantic Kernel plugin functions invoked while the
/// response streams.
///
/// A single instance is shared as a singleton; the per-flow isolation lives in
/// the <see cref="AsyncLocal{T}"/> itself, so two concurrent turns never observe
/// each other's identity.
/// </summary>
public sealed class AsyncLocalScoutRequestContext : IScoutRequestContext
{
    private readonly AsyncLocal<ScoutRequestIdentity?> _current = new();

    /// <inheritdoc />
    public ScoutRequestIdentity? Current => _current.Value;

    /// <inheritdoc />
    public IDisposable BeginScope(ScoutRequestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // Capture the previous value so nested scopes restore in stack order.
        var previous = _current.Value;
        _current.Value = identity;
        return new Scope(this, previous);
    }

    // Restores the captured previous value on Dispose. Idempotent so a double
    // dispose (or a dispose after an outer scope already restored) is harmless.
    private sealed class Scope(AsyncLocalScoutRequestContext owner, ScoutRequestIdentity? previous)
        : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner._current.Value = previous;
        }
    }
}
