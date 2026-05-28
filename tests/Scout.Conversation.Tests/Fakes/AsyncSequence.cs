using System.Runtime.CompilerServices;

namespace Scout.Conversation.Tests.Fakes;

/// <summary>Helpers to build controllable token sequences for pump tests.</summary>
internal static class AsyncSequence
{
    /// <summary>Yields each chunk, honoring cancellation between chunks.</summary>
    public static async IAsyncEnumerable<string> Of(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }

    /// <summary>Yields the given chunks, then throws the supplied exception.</summary>
    public static async IAsyncEnumerable<string> ThenThrow(
        Exception toThrow,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
        throw toThrow;
    }

    /// <summary>
    /// Yields the first chunk, cancels the supplied source, then would yield
    /// more — used to assert cancellation mid-stream surfaces and stops.
    /// </summary>
    public static async IAsyncEnumerable<string> CancelAfterFirst(
        CancellationTokenSource cts,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        params string[] chunks)
    {
        var first = true;
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
            if (first)
            {
                first = false;
                await cts.CancelAsync();
            }
        }
    }
}
