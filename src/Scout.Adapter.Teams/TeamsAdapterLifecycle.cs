using System.Diagnostics.CodeAnalysis;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams;

/// <summary>
/// Start/stop lifecycle state for <see cref="TeamsChatAdapter"/>: the running state, the handler of the start and a lifetime
/// <see cref="CancellationToken"/> that trips on stop so in-flight conversations unwind on host shutdown, published and
/// cleared together under one lock, so a turn that sees the adapter running always gets the handler of that start.
/// Extracted so the lifecycle is unit-testable without any HTTP host.
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
    private Func<ChatMessage, IResponseSink, CancellationToken, Task>? _handler;
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
    /// The handler of the running start and its lifetime token, read in one step with the running state; false, with no
    /// handler and a default token, before a start or once a stop has begun, so a turn racing a stop sees a clean shutdown
    /// and a turn racing a start sees either a stopped adapter or the handler of that start, never a running one without it.
    /// </summary>
    public bool TryGetSession(
        [NotNullWhen(true)] out Func<ChatMessage, IResponseSink, CancellationToken, Task>? handler, out CancellationToken token)
    {
        lock (_gate)
        {
            if (_running && _lifetimeCts is not null && _handler is not null)
            {
                handler = _handler;
                token = _lifetimeCts.Token;
                return true;
            }

            handler = null;
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
    /// Transition to started with <paramref name="handler"/>, linking the lifetime token to
    /// <paramref name="hostToken"/>; the handler and the token are visible with the running state. Throws if already running.
    /// </summary>
    public void Start(Func<ChatMessage, IResponseSink, CancellationToken, Task> handler, CancellationToken hostToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException(
                    $"{nameof(TeamsChatAdapter)} has already been started.");
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _handler = handler;
            _running = true;
            SessionPublished?.Invoke();
        }
    }

    /// <summary>
    /// Transition to stopped, detach the handler and cancel the lifetime token. Returns false (no-op)
    /// when not currently running. A later <see cref="Start"/> is allowed, and its handler is never cleared by this stop.
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
            _handler = null;
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

    /// <summary>
    /// Runs inside <see cref="Start"/> while its lock is held, once the running state, the handler and the token are
    /// published; null outside tests. Exists only so a test can deliver a turn at that point, and must never be used to
    /// change the lifecycle or to run production work.
    /// </summary>
    internal Action? SessionPublished { get; set; }
}
