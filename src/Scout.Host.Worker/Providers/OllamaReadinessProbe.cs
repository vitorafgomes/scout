namespace Scout.Host.Worker.Providers;

/// <summary>
/// Singleton probe that gates consumers of the local Ollama runtime so they
/// only ping after <see cref="LocalRuntimeAutostartService"/> has finished
/// its warmup. Mark transitions are CAS-protected and idempotent.
/// </summary>
public sealed class OllamaReadinessProbe : IOllamaReadinessProbe
{
    private readonly TaskCompletionSource<bool> _gate =
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

    internal void MarkStarting()
    {
        Interlocked.CompareExchange(
            ref _state,
            (int)OllamaReadinessState.Starting,
            (int)OllamaReadinessState.Unknown);
    }

    internal void MarkReady()
    {
        if (TryAdvanceTo(OllamaReadinessState.Ready))
        {
            _gate.TrySetResult(true);
        }
    }

    internal void MarkFailed()
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
