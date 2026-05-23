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

    /// <summary>
    /// Transitions to <see cref="OllamaReadinessState.Starting"/>. From Failed
    /// the gate is reset so new waiters block again; from Ready or Starting
    /// this is a no-op.
    /// </summary>
    void MarkStarting();

    /// <summary>
    /// Transitions to <see cref="OllamaReadinessState.Ready"/> and completes
    /// pending waiters with true. Idempotent.
    /// </summary>
    void MarkReady();

    /// <summary>
    /// Transitions to <see cref="OllamaReadinessState.Failed"/> and completes
    /// pending waiters with false. Idempotent; can be re-armed via
    /// <see cref="MarkStarting"/>.
    /// </summary>
    void MarkFailed();
}
