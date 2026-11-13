using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// Signals readiness, then warms the meetings store in the background with one list of a single meeting through
/// <see cref="IMeetingStore"/>, so the store's schema check and Entity Framework's first query are paid before the
/// owner's first meetings request. The warm-up never delays the readiness marker, and its failure is logged at Debug
/// with the exception type only; the next real call tries the schema again. The audit store needs none: the audit worker
/// reads the running jobs through its store as it starts.
/// </summary>
internal sealed class StoreWarmupReadinessSignal(
    IReadinessSignal inner,
    IMeetingStore? meetings,
    ILogger<StoreWarmupReadinessSignal> logger) : IReadinessSignal
{
    /// <summary>The warm-up started by the last <see cref="Signal"/>; completed when there is nothing to warm.</summary>
    internal Task Warmed { get; private set; } = Task.CompletedTask;

    public void Signal()
    {
        inner.Signal();
        if (meetings is not null)
        {
            Warmed = Task.Run(() => WarmAsync(meetings));
        }
    }

    private async Task WarmAsync(IMeetingStore store)
    {
        try
        {
            await store.ListAsync(1, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Warming the meetings store failed ({ExceptionType}); the first meetings request will try again.", ex.GetType().Name);
        }
    }
}
