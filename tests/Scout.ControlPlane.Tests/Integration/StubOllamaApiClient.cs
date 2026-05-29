using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Scout.ControlPlane.Providers;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// Hermetic stand-in for <see cref="IOllamaApiClient"/>. No network is touched:
/// tags and model list come from in-memory state, and the pull stream is fed
/// frame-by-frame from a channel the test controls. The pull enumerator records
/// whether it observed cancellation so a test can assert the server aborted the
/// underlying work rather than running it to completion.
/// </summary>
internal sealed class StubOllamaApiClient : IOllamaApiClient
{
    private readonly Channel<PullProgress> _pullFrames =
        Channel.CreateUnbounded<PullProgress>();

    /// <summary>Tags returned by <see cref="GetTagsAsync"/>; null means unreachable.</summary>
    public OllamaTags? Tags { get; set; } = new(Array.Empty<ProviderModel>());

    /// <summary>Set true once the pull enumerator observed cancellation.</summary>
    public bool PullWasCancelled { get; private set; }

    /// <summary>Set true once the pull enumerator ran to natural completion.</summary>
    public bool PullCompleted { get; private set; }

    /// <summary>Models deleted via <see cref="DeleteModelAsync"/>.</summary>
    public List<string> DeletedModels { get; } = new();

    public Task<OllamaTags?> GetTagsAsync(CancellationToken ct) => Task.FromResult(Tags);

    /// <summary>Push one progress frame to any live pull stream.</summary>
    public ValueTask EmitAsync(PullProgress frame) => _pullFrames.Writer.WriteAsync(frame);

    /// <summary>Signal natural end-of-stream for the current pull.</summary>
    public void CompletePull() => _pullFrames.Writer.TryComplete();

    public async IAsyncEnumerable<PullProgress> PullAsync(
        string modelName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _pullFrames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return frame;
            }
            PullCompleted = true;
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                PullWasCancelled = true;
            }
        }
    }

    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        DeletedModels.Add(modelName);
        return Task.CompletedTask;
    }
}
