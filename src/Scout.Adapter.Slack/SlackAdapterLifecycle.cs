namespace Scout.Adapter.Slack;

/// <summary>
/// Single-connect lifecycle state machine for <see cref="SlackChatAdapter"/>.
/// Owns the start/stop transitions and a lifetime <see cref="CancellationToken"/>
/// that trips on stop so in-flight conversations unwind on host shutdown.
/// Extracted from the adapter so the lifecycle is unit-testable without a real
/// Socket Mode connection. A start after a stop is rejected: the disposed
/// socket-mode client cannot be reconnected.
/// </summary>
internal sealed class SlackAdapterLifecycle : IDisposable
{
    private const int StateIdle = 0;
    private const int StateStarted = 1;
    private const int StateStopped = 2;

    private CancellationTokenSource? _lifetimeCts;
    private int _state;

    /// <summary>Token tied to the active session; cancelled by <see cref="StopAsync"/>.</summary>
    public CancellationToken LifetimeToken =>
        _lifetimeCts?.Token ?? throw new InvalidOperationException("Lifecycle not started.");

    /// <summary>True between a successful <see cref="Start"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning => Volatile.Read(ref _state) == StateStarted;

    /// <summary>
    /// Transition to started, linking the lifetime token to
    /// <paramref name="hostToken"/>. Throws if already started or stopped.
    /// </summary>
    public void Start(CancellationToken hostToken)
    {
        var previous = Interlocked.CompareExchange(ref _state, StateStarted, StateIdle);
        if (previous == StateStarted)
        {
            throw new InvalidOperationException(
                $"{nameof(SlackChatAdapter)} has already been started.");
        }
        if (previous == StateStopped)
        {
            throw new InvalidOperationException(
                $"{nameof(SlackChatAdapter)} cannot be restarted after being stopped.");
        }

        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
    }

    /// <summary>
    /// Transition to stopped and cancel the lifetime token. Returns false (no-op)
    /// when not currently running. Terminal: a later <see cref="Start"/> throws.
    /// </summary>
    public async Task<bool> StopAsync()
    {
        if (Interlocked.CompareExchange(ref _state, StateStopped, StateStarted) != StateStarted)
        {
            return false;
        }

        if (_lifetimeCts is not null)
        {
            await _lifetimeCts.CancelAsync().ConfigureAwait(false);
            _lifetimeCts.Dispose();
            _lifetimeCts = null;
        }
        return true;
    }

    public void Dispose() => _lifetimeCts?.Dispose();
}
