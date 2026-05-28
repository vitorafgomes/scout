using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Scout.Adapter.Discord.Streaming;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;

namespace Scout.Adapter.Discord.Tests.Streaming;

/// <summary>
/// Drives the real <see cref="DiscordResponseSink"/> through the
/// <see cref="StreamPump"/> with a fake edit delegate, injecting transport
/// failures (429, 5xx) on interim edits. Hermetic — no Discord.Net network.
/// </summary>
public sealed class DiscordSinkBoundaryTests
{
    private const string Cursor = "▌";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero));

    // Advances the fake clock by one interval before each chunk so every chunk
    // crosses the edit cadence.
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

    private static StreamPump Pump(FakeTimeProvider time) => new(new StreamPumpOptions
    {
        EditInterval = Interval,
        Cursor = Cursor,
        TimeProvider = time,
    });

    private sealed class FakeRateLimitedException(TimeSpan retryAfter) : Exception("rate limited")
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }

    [Fact]
    public async Task Edit_429_MidStream_Retries_And_Body_Stays_Monotonic()
    {
        var time = Clock();
        var edits = new List<string>();
        var editCall = 0;
        var raised = false;

        // The sink's interim edits go through this delegate. The 2nd interim
        // edit hits a rate limit; the pump must back off and retry.
        var sink = new DiscordResponseSink(
            postInitial: (_, _) => Task.CompletedTask,
            edit: (body, _) =>
            {
                editCall++;
                if (editCall == 2 && !raised)
                {
                    raised = true;
                    throw new FakeRateLimitedException(TimeSpan.FromMilliseconds(200));
                }
                edits.Add(body);
                return Task.CompletedTask;
            });

        var response = new ChatResponse(Ticking(time, default, "a", "b", "c"));

        var run = Pump(time).RunAsync(response, sink, CancellationToken.None);
        while (!run.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(50));
        }
        await run;

        raised.ShouldBeTrue();
        edits.ShouldNotBeEmpty();

        // The final edit (Complete) carries the full coherent body, no cursor.
        edits[^1].ShouldBe("abc");

        // Cumulative bodies (cursor stripped) are monotonic.
        var stripped = edits.Select(e => e.Replace(Cursor, string.Empty)).ToList();
        for (var i = 1; i < stripped.Count; i++)
        {
            stripped[i].ShouldStartWith(stripped[i - 1]);
        }
    }

    [Fact]
    public async Task Edit_5xx_MidStream_Drops_Edit_No_Throw_Finalize_Coherent()
    {
        var time = Clock();
        var edits = new List<string>();
        var editCall = 0;

        var sink = new DiscordResponseSink(
            postInitial: (_, _) => Task.CompletedTask,
            edit: (body, _) =>
            {
                editCall++;
                if (editCall == 2)
                {
                    // Non-rate-limit 5xx: pump drops this interim edit, keeps
                    // the buffer, and must not throw into the host.
                    throw new HttpRequestException("Discord returned 500");
                }
                edits.Add(body);
                return Task.CompletedTask;
            });

        var response = new ChatResponse(Ticking(time, default, "a", "b", "c"));

        await Should.NotThrowAsync(
            () => Pump(time).RunAsync(response, sink, CancellationToken.None));

        // The dropped interim is absent, but the final edit is coherent.
        edits[^1].ShouldBe("abc");
    }
}
