using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Tests.Streaming;

/// <summary>
/// Boundary failure injection at the stream and sink edges. The pump is
/// transport-agnostic: it does not retry; a non-cancellation interim throw is
/// swallowed (the transport owns its own resilience), the buffer is retained,
/// and finalize stays coherent. Hermetic — no real transport.
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

    private static StreamPump Pump(FakeTimeProvider time, ILogger? logger = null)
        => new(new StreamPumpOptions
        {
            EditInterval = Interval,
            Cursor = Cursor,
            TimeProvider = time,
            Logger = logger,
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

    [Fact]
    public async Task Transient_Interim_Update_Throw_Is_Swallowed_Finalize_Coherent()
    {
        var time = Clock();
        // Any non-cancellation interim failure is dropped by the pump; the
        // buffer is retained and the final body is still coherent. The pump
        // itself does not retry — that is the transport's (sink's) job.
        var sink = new RecordingResponseSink(FineGrained, onUpdate: (index, _) =>
        {
            if (index == 1)
            {
                throw new InvalidOperationException("transient transport failure");
            }
        });

        var response = new ChatResponse(Ticking(time, default, "a", "b", "c"));

        await Should.NotThrowAsync(
            () => Pump(time).RunAsync(response, sink, CancellationToken.None));

        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
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

    [Fact]
    public async Task RateLimit_MidStream_Finalizes_With_Friendly_Message_And_Quiet_Log()
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new LlmRateLimitException("LLM rate limit (HTTP 429)."),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time, logger).RunAsync(response, sink, CancellationToken.None));

        // Finalized via Complete (not Fail) so the user sees the friendly text.
        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldNotBeNull();
        sink.FinalText!.ShouldContain("rate limit");
        sink.FinalText!.ShouldContain("partial");

        // Quiet log: nothing logged with an exception and no Warning-level noise.
        logger.Entries.Any(e => e.Exception != null).ShouldBeFalse();
        logger.EntriesAt(LogLevel.Warning).ShouldBeEmpty();
    }

    [Fact]
    public async Task Generic_Failure_MidStream_Still_Warns_With_Stack_And_Fails()
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new InvalidOperationException("connection reset"),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time, logger).RunAsync(response, sink, CancellationToken.None));

        sink.Failed.ShouldBeTrue();
        sink.FailReason.ShouldNotBeNull();
        sink.FailReason!.ShouldContain("connection reset");

        // Generic failures keep the stack: a Warning entry carrying the exception.
        logger.EntriesAt(LogLevel.Warning).Any(e => e.Exception != null).ShouldBeTrue();
    }
}
