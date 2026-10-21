namespace Scout.Adapter.Teams;

/// <summary>
/// Start/stop lifecycle state for <see cref="TeamsChatAdapter"/>, with a lifetime
/// <see cref="CancellationToken"/> that trips on stop so in-flight conversations
/// unwind on host shutdown. Extracted so the lifecycle is unit-testable without
/// any HTTP host.
///
/// Unlike Discord (one socket login per session), Teams is webhook-push: the
/// adapter has no connection of its own, so a restart after stop is permitted —
/// the messaging endpoint can be re-armed. Start while already running is still
/// rejected.
/// </summary>
internal sealed class TeamsAdapterLifecycle : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _lifetimeCts;
    private bool _running;

    /// <summary>Token tied to the active session; cancelled by <see cref="StopAsync"/>.</summary>
    public CancellationToken LifetimeToken
    {
        get
        {
            lock (_gate)
            {
                return _lifetimeCts?.Token
                    ?? throw new InvalidOperationException("Lifecycle not started.");
            }
        }
    }

    /// <summary>
    /// Atomically read the lifetime token while running. Returns false (and a
    /// default token) when not running, so a caller racing <see cref="StopAsync"/>
    /// sees a clean shutdown rather than the <see cref="LifetimeToken"/> throw.
    /// </summary>
    public bool TryGetLifetimeToken(out CancellationToken token)
    {
        lock (_gate)
        {
            if (_running && _lifetimeCts is not null)
            {
                token = _lifetimeCts.Token;
                return true;
            }
            token = default;
            return false;
        }
    }

    /// <summary>True between a successful <see cref="Start"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// Transition to started, linking the lifetime token to
    /// <paramref name="hostToken"/>. Throws if already running.
    /// </summary>
    public void Start(CancellationToken hostToken)
    {
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException(
                    $"{nameof(TeamsChatAdapter)} has already been started.");
            }
            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _running = true;
        }
    }

    /// <summary>
    /// Transition to stopped and cancel the lifetime token. Returns false (no-op)
    /// when not currently running. A later <see cref="Start"/> is allowed.
    /// </summary>
    public async Task<bool> StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_running)
            {
                return false;
            }
            _running = false;
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
