using Scout.ControlPlane.Providers;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>Readiness probe that is permanently Ready; no daemon is involved.</summary>
internal sealed class AlwaysReadyProbe : IOllamaReadinessProbe
{
    public OllamaReadinessState Current => OllamaReadinessState.Ready;

    public bool IsReady => true;

    public Task WaitReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public void MarkStarting()
    {
    }

    public void MarkReady()
    {
    }

    public void MarkFailed()
    {
    }
}
