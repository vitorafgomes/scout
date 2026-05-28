using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;

namespace Scout.Conversation.Tests.Streaming;

/// <summary>
/// Boundary failure injection at the stream and sink edges. Hermetic — no real
/// transport.
/// </summary>
public sealed class StreamPumpFailureTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);
    private const string Cursor = "▌";

    private static readonly RenderCapabilities FineGrained = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = 4096,
    };

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero));

    private static StreamPump Pump(FakeTimeProvider time)
        => new(new StreamPumpOptions
        {
            EditInterval = Interval,
            Cursor = Cursor,
            TimeProvider = time,
        });

    private static async IAsyncEnumerable<string> Ticking(
        FakeTimeProvider time,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            time.Advance(Interval);
            await Task.Yield();
            yield return chunk;
        }
    }

    private sealed class FakeRateLimitedException(TimeSpan retryAfter) : Exception("rate limited")
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }

    [Fact]
    public async Task Sink_429_MidStream_Retries_And_Stays_Monotonic_And_Completes()
    {
        var time = Clock();
        var raised = false;
        // Throw a rate-limit on the 2nd interim update; retry must then succeed.
        var sink = new RecordingResponseSink(FineGrained, onUpdate: (index, _) =>
        {
            if (index == 1 && !raised)
            {
                raised = true;
                throw new FakeRateLimitedException(TimeSpan.FromMilliseconds(200));
            }
        });

        var response = new ChatResponse(Ticking(time, default, "a", "b", "c"));

        var run = Pump(time).RunAsync(response, sink, CancellationToken.None);

        // Let the retry register its Task.Delay against the fake clock.
        while (!run.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(50));
        }
        await run;

        raised.ShouldBeTrue();
        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);

        // Cumulative bodies recorded by the sink remain monotonic.
        var stripped = sink.Updates.Select(u => u.Replace(Cursor, string.Empty)).ToList();
        for (var i = 1; i < stripped.Count; i++)
        {
            stripped[i].ShouldStartWith(stripped[i - 1]);
        }
        sink.FinalText.ShouldBe("abc");
    }

    [Fact]
    public async Task Sink_5xx_MidStream_Drops_Edit_Does_Not_Throw_Finalize_Coherent()
    {
        var time = Clock();
        // Non-rate-limit failure on the 2nd interim update: pump must swallow it,
        // keep the buffer, and still finalize coherently.
        var sink = new RecordingResponseSink(FineGrained, onUpdate: (index, _) =>
        {
            if (index == 1)
            {
                throw new InvalidOperationException("HTTP 500 from transport");
            }
        });

        var response = new ChatResponse(Ticking(time, default, "a", "b", "c"));

        await Should.NotThrowAsync(
            () => Pump(time).RunAsync(response, sink, CancellationToken.None));

        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.FinalText.ShouldBe("abc");
    }

    [Fact]
    public async Task Stream_Throws_MidStream_Fails_With_Partial_Buffer_No_Complete()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new InvalidOperationException("connection reset"),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time).RunAsync(response, sink, CancellationToken.None));

        sink.Failed.ShouldBeTrue();
        sink.Completed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldBe("partial");
        sink.FailReason.ShouldNotBeNull();
        sink.FailReason!.ShouldContain("connection reset");
    }

    [Fact]
    public async Task Cancellation_MidStream_Propagates_No_Forced_Terminal_Edit()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        using var cts = new CancellationTokenSource();
        var response = new ChatResponse(
            AsyncSequence.CancelAfterFirst(cts, default, "first", "second", "third"));

        await Should.ThrowAsync<OperationCanceledException>(
            () => Pump(time).RunAsync(response, sink, cts.Token));

        // No terminal edit was forced; sink is not corrupted (Begin happened,
        // no Complete/Fail).
        sink.BeginCount.ShouldBe(1);
        sink.Completed.ShouldBeFalse();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(0);
    }
}
