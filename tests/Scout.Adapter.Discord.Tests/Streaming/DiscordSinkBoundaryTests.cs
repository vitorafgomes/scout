using Discord.Net;
using Microsoft.Extensions.Time.Testing;
using Scout.Adapter.Discord.Streaming;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord.Tests.Streaming;

/// <summary>
/// Exercises the Discord sink's OWN transport resilience (Discord.Net 429
/// backoff-and-retry, 5xx drop on interim) directly, with an injected edit
/// delegate and a FakeTimeProvider. Hermetic — no Discord.Net network. Rate
/// limits are the sink's concern now, not the pump's.
/// </summary>
public sealed class DiscordSinkBoundaryTests
{
    private const string Cursor = "▌";

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero));

    private static RateLimitedException RateLimited()
        => new(Substitute.For<IRequest>());

    [Fact]
    public async Task Update_429_Backs_Off_On_FakeClock_Then_Retries_Once()
    {
        var time = Clock();
        var edits = new List<string>();
        var attempts = 0;

        var sink = new DiscordResponseSink(
            postInitial: (_, _) => Task.CompletedTask,
            edit: (body, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw RateLimited();
                }
                edits.Add(body);
                return Task.CompletedTask;
            },
            timeProvider: time);

        await sink.BeginAsync(CancellationToken.None);

        var update = sink.UpdateAsync("hello", CancellationToken.None);
        // The retry registers a Task.Delay against the fake clock; advance it.
        while (!update.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(250));
        }
        await update;

        attempts.ShouldBe(2);
        edits.ShouldHaveSingleItem();
        edits[0].ShouldBe("hello" + Cursor);
    }

    [Fact]
    public async Task Complete_429_Backs_Off_Then_Retries_So_Terminal_Lands()
    {
        var time = Clock();
        var edits = new List<string>();
        var attempts = 0;

        var sink = new DiscordResponseSink(
            postInitial: (_, _) => Task.CompletedTask,
            edit: (body, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw RateLimited();
                }
                edits.Add(body);
                return Task.CompletedTask;
            },
            timeProvider: time);

        await sink.BeginAsync(CancellationToken.None);

        var complete = sink.CompleteAsync("done", Array.Empty<Citation>(), CancellationToken.None);
        while (!complete.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(250));
        }
        await complete;

        attempts.ShouldBe(2);
        edits.ShouldHaveSingleItem();
        edits[0].ShouldBe("done");
    }

    [Fact]
    public async Task Update_5xx_Is_Dropped_Does_Not_Throw_Terminal_Still_Coherent()
    {
        var time = Clock();
        var edits = new List<string>();

        var sink = new DiscordResponseSink(
            postInitial: (_, _) => Task.CompletedTask,
            edit: (body, _) =>
            {
                if (body.Contains("drop", StringComparison.Ordinal))
                {
                    // Non-rate-limit transport error on an interim edit.
                    throw new HttpRequestException("Discord returned 500");
                }
                edits.Add(body);
                return Task.CompletedTask;
            },
            timeProvider: time);

        await sink.BeginAsync(CancellationToken.None);

        // This interim edit fails with a 5xx and must be swallowed by the sink.
        await Should.NotThrowAsync(() => sink.UpdateAsync("drop-me", CancellationToken.None));

        // A later interim and the terminal still render coherently.
        await sink.UpdateAsync("kept", CancellationToken.None);
        await sink.CompleteAsync("final", Array.Empty<Citation>(), CancellationToken.None);

        edits.ShouldBe(["kept" + Cursor, "final"]);
    }
}
