namespace Scout.ControlPlane.Providers;

/// <summary>
/// Singleton probe that gates consumers of the local Ollama runtime so they
/// only ping after the autostart warmup has finished. Mark transitions are
/// CAS-protected and idempotent. The probe is recoverable: a Failed state can
/// be reset to Starting on a user-initiated lifecycle action without
/// restarting the worker.
/// </summary>
public sealed class OllamaReadinessProbe : IOllamaReadinessProbe
{
    // Mutable reference: replaced with a fresh, uncompleted TCS each time the
    // probe is reset out of Failed. Reads in WaitReadyAsync snapshot the field
    // once so they observe a consistent gate.
    private volatile TaskCompletionSource<bool> _gate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _state = (int)OllamaReadinessState.Unknown;

    public OllamaReadinessState Current => (OllamaReadinessState)Volatile.Read(ref _state);

    public bool IsReady => Current == OllamaReadinessState.Ready;

    public Task WaitReadyAsync(CancellationToken cancellationToken)
    {
        return _gate.Task.WaitAsync(cancellationToken);
    }

    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _gate.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void MarkStarting()
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (current == (int)OllamaReadinessState.Ready ||
                current == (int)OllamaReadinessState.Starting)
            {
                return;
            }

            // From Failed we must give new waiters a fresh, uncompleted gate
            // BEFORE the state transition becomes observable. Otherwise a
            // waiter that lands between the CAS and the gate swap would
            // synchronously consume the stale `false` completion.
            if (current == (int)OllamaReadinessState.Failed)
            {
                var fresh = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _gate = fresh;
            }

            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)OllamaReadinessState.Starting,
                    current) == current)
            {
                return;
            }
        }
    }

    public void MarkReady()
    {
        if (TryAdvanceTo(OllamaReadinessState.Ready))
        {
            _gate.TrySetResult(true);
        }
    }

    public void MarkFailed()
    {
        if (TryAdvanceTo(OllamaReadinessState.Failed))
        {
            _gate.TrySetResult(false);
        }
    }

    private bool TryAdvanceTo(OllamaReadinessState target)
    {
        var targetInt = (int)target;
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (current == (int)OllamaReadinessState.Ready ||
                current == (int)OllamaReadinessState.Failed)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _state, targetInt, current) == current)
            {
                return true;
            }
        }
    }
}
