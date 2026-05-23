namespace Scout.Host.Worker.Providers;

/// <summary>
/// Exposes the readiness state of the local Ollama runtime so consumers can
/// wait for autostart to finish before issuing transport calls.
/// </summary>
public interface IOllamaReadinessProbe
{
    OllamaReadinessState Current { get; }

    bool IsReady { get; }

    /// <summary>
    /// Completes when the probe transitions to <see cref="OllamaReadinessState.Ready"/>
    /// or <see cref="OllamaReadinessState.Failed"/>. Does not throw on Failed.
    /// Cancellation throws as usual.
    /// </summary>
    Task WaitReadyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns true when Ready before the deadline, false on Failed or timeout.
    /// Never throws unless cancellation is requested via <paramref name="cancellationToken"/>.
    /// </summary>
    Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
