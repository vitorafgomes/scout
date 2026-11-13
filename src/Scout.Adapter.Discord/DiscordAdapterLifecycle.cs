namespace Scout.Adapter.Discord;

/// <summary>
/// Single-login lifecycle state machine for <see cref="DiscordChatAdapter"/>.
/// Owns the start/stop transitions and a lifetime <see cref="CancellationToken"/>
/// that trips on stop so in-flight conversations unwind on host shutdown.
/// Extracted from the adapter so the lifecycle is unit-testable without a real
/// Discord login. Discord.Net does not support re-login on a stopped session, so
/// a start after a stop is rejected.
/// </summary>
internal sealed class DiscordAdapterLifecycle : IDisposable
{
    private const int StateIdle = 0;
    private const int StateStarted = 1;
    private const int StateStopped = 2;

    private readonly Lock _gate = new();
    private CancellationTokenSource? _lifetimeCts;
    private int _state;

    /// <summary>Token tied to the active session; cancelled by <see cref="StopAsync"/>.</summary>
    public CancellationToken LifetimeToken
    {
        get
        {
            lock (_gate)
            {
                return _lifetimeCts?.Token ?? throw new InvalidOperationException("Lifecycle not started.");
            }
        }
    }

    /// <summary>True between a successful <see cref="Start"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning => Volatile.Read(ref _state) == StateStarted;

    /// <summary>
    /// The lifetime token while running, read in one step with the running state; false, and no token, before a start
    /// or once a stop has begun, so a caller a stop overtakes sees a stopped adapter and never a missing token.
    /// </summary>
    public bool TryGetLifetimeToken(out CancellationToken token)
    {
        lock (_gate)
        {
            if (_state == StateStarted && _lifetimeCts is not null)
            {
                token = _lifetimeCts.Token;
                return true;
            }

            token = default;
            return false;
        }
    }

    /// <summary>
    /// Transition to started, linking the lifetime token to
    /// <paramref name="hostToken"/>; the token exists before the started state is
    /// visible. Throws if already started or stopped.
    /// </summary>
    public void Start(CancellationToken hostToken)
    {
        lock (_gate)
        {
            if (_state != StateIdle)
            {
                throw new InvalidOperationException(_state == StateStarted
                    ? $"{nameof(DiscordChatAdapter)} has already been started."
                    : $"{nameof(DiscordChatAdapter)} cannot be restarted after being stopped.");
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            Volatile.Write(ref _state, StateStarted);
        }
    }

    /// <summary>
    /// Transition to stopped and cancel the lifetime token. Returns false (no-op)
    /// when not currently running. Terminal: a later <see cref="Start"/> throws.
    /// </summary>
    public async Task<bool> StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_state != StateStarted)
            {
                return false;
            }

            Volatile.Write(ref _state, StateStopped);
            cts = _lifetimeCts;
            _lifetimeCts = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
        }
    }
}
