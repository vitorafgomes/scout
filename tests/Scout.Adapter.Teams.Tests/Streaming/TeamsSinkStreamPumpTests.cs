using System.Runtime.CompilerServices;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Time.Testing;
using Scout.Adapter.Teams.Streaming;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;

namespace Scout.Adapter.Teams.Tests.Streaming;

/// <summary>
/// Drives the Teams sink through the REAL <see cref="StreamPump"/> with a
/// FakeTimeProvider, proving the locked contract expresses Teams: throttled
/// interim cumulative typing activities, the 2-min MaxResponseDuration folds in
/// and finalizes early, and a mid-stream gateway failure folds into a Fail with
/// the partial buffer. No network, no Azure.
/// </summary>
public sealed class TeamsSinkStreamPumpTests
{
    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero));

    private static StreamPump Pump(FakeTimeProvider time, TimeSpan? overallTimeout = null) =>
        new(new StreamPumpOptions
        {
            EditInterval = TimeSpan.FromMilliseconds(750),
            OverallTimeout = overallTimeout,
            TimeProvider = time,
            Cursor = string.Empty,
        });

    [Fact]
    public async Task Pump_Sends_Throttled_Interim_Cumulative_Updates_Then_Final_With_Citations()
    {
        var time = Clock();
        var sender = new RecordingSender((_, i) => i == 0 ? "stream-1" : null);
        var sink = new TeamsResponseSink(sender, timeProvider: time);

        // Three tokens, each separated by ~1.5s so the ~1s floor lets each through.
        var response = new ChatResponse(
            Tokens(time, ("Hello", 1500), (" world", 1500), (" answer", 1500)),
            new[] { new Citation("src/A.cs:1") { Path = "src/A.cs", Line = 1 } });

        await Pump(time).RunAsync(response, sink, CancellationToken.None);

        // Begin informative + interim streaming activities + final message.
        var informative = sender.Sent.First();
        RecordingSender.StreamType(informative).ShouldBe("informative");

        var streaming = sender.Sent
            .Where(a => RecordingSender.StreamType(a) == "streaming")
            .ToList();
        streaming.ShouldNotBeEmpty();
        // Cumulative, monotonic text on the interim activities.
        streaming[^1].Text.ShouldBe("Hello world answer");

        // Sequence numbers strictly increase across the whole stream.
        var sequences = sender.Sent.Select(a => RecordingSender.StreamSequence(a)!.Value).ToList();
        sequences.ShouldBe(sequences.OrderBy(x => x).ToList());
        sequences.ShouldBe(sequences.Distinct().ToList());

        // Final message carries the full text and citations only at the end.
        var final = sender.Sent[^1];
        final.Type.ShouldBe(ActivityTypes.Message);
        RecordingSender.StreamType(final).ShouldBe("final");
        final.Text.ShouldBe("Hello world answer");
        final.Attachments.ShouldNotBeNull();
    }

    [Fact]
    public async Task Pump_Finalizes_Early_When_2min_MaxResponseDuration_Elapses()
    {
        var time = Clock();
        var sender = new RecordingSender((_, i) => i == 0 ? "stream-1" : null);
        var sink = new TeamsResponseSink(sender, timeProvider: time);

        // A stream that would otherwise run for 10 minutes. The sink's
        // MaxResponseDuration (120s) folds with the pump timeout (min wins) and
        // trips the pump, which finalizes with the partial buffer via FailAsync.
        var response = new ChatResponse(Slow(time, TimeSpan.FromMinutes(10)));

        await Pump(time, overallTimeout: TimeSpan.FromMinutes(30))
            .RunAsync(response, sink, CancellationToken.None);

        var final = sender.Sent[^1];
        final.Type.ShouldBe(ActivityTypes.Message);
        RecordingSender.StreamType(final).ShouldBe("final");
        // Timed out before the 10-minute stream completed: terminal still lands.
        final.Text.ShouldContain("response interrupted");
    }

    [Fact]
    public async Task Pump_Gateway_Throws_MidStream_Folds_Into_Fail_With_Partial()
    {
        var time = Clock();
        var sender = new RecordingSender((_, i) => i == 0 ? "stream-1" : null);
        var sink = new TeamsResponseSink(sender, timeProvider: time);

        var response = new ChatResponse(ThrowsMidStream(time));

        await Pump(time).RunAsync(response, sink, CancellationToken.None);

        var final = sender.Sent[^1];
        final.Type.ShouldBe(ActivityTypes.Message);
        RecordingSender.StreamType(final).ShouldBe("final");
        // The partial text accumulated before the gateway threw is preserved.
        final.Text.ShouldStartWith("partial");
        final.Text.ShouldContain("response interrupted");
    }

    private static async IAsyncEnumerable<string> Tokens(
        FakeTimeProvider time,
        params (string Text, int DelayMs)[] tokens)
    {
        foreach (var (text, delayMs) in tokens)
        {
            time.Advance(TimeSpan.FromMilliseconds(delayMs));
            yield return text;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<string> Slow(
        FakeTimeProvider time,
        TimeSpan total,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var elapsed = TimeSpan.Zero;
        var step = TimeSpan.FromSeconds(10);
        while (elapsed < total)
        {
            ct.ThrowIfCancellationRequested();
            // Advancing past 120s fires the folded timeout CTS; the pump observes
            // cancellation on the next iteration and finalizes via FailAsync.
            time.Advance(step);
            elapsed += step;
            yield return "chunk ";
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<string> ThrowsMidStream(FakeTimeProvider time)
    {
        time.Advance(TimeSpan.FromMilliseconds(1500));
        yield return "partial";
        await Task.Yield();
        // Simulate the upstream gateway failing mid-stream.
        throw new InvalidOperationException("gateway exploded");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
