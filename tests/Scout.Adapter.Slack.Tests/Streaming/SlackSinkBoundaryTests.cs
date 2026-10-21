using Microsoft.Extensions.Time.Testing;
using Scout.Adapter.Slack.Streaming;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using SlackNet;

namespace Scout.Adapter.Slack.Tests.Streaming;

/// <summary>
/// Exercises the Slack sink's OWN transport resilience (Slack 429 Retry-After
/// backoff-and-retry, 5xx drop on interim) directly, with injected post/edit
/// delegates and a FakeTimeProvider. Hermetic — no Slack network. Rate limits
/// are the sink's concern, not the pump's. Where practical the sink is driven
/// through the REAL <see cref="StreamPump"/>.
/// </summary>
public sealed class SlackSinkBoundaryTests
{
    private const string Cursor = "▌";
    private static readonly SlackMessageHandle Handle = new("C1", "1700000000.000100");

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero));

    private static SlackRateLimitException RateLimited(TimeSpan? retryAfter = null)
        => new(retryAfter);

    private static Func<string, CancellationToken, Task<SlackMessageHandle>> OkPost(List<string> posts)
        => (body, _) => { posts.Add(body); return Task.FromResult(Handle); };

    [Fact]
    public async Task Update_429_Backs_Off_For_RetryAfter_On_FakeClock_Then_Retries_Once()
    {
        var time = Clock();
        var edits = new List<string>();
        var attempts = 0;
        var retryAfter = TimeSpan.FromSeconds(3);

        var sink = new SlackResponseSink(
            postInitial: OkPost(new List<string>()),
            edit: (_, body, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw RateLimited(retryAfter);
                }
                edits.Add(body);
                return Task.CompletedTask;
            },
            timeProvider: time);

        await sink.BeginAsync(CancellationToken.None);

        var update = sink.UpdateAsync("hello", CancellationToken.None);
        // The retry registers a Task.Delay(retryAfter) against the fake clock.
        while (!update.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(500));
        }
        await update;

        attempts.ShouldBe(2);
        edits.ShouldHaveSingleItem();
        edits[0].ShouldBe("hello" + Cursor);
    }

    [Fact]
    public async Task Update_429_Without_RetryAfter_Uses_Default_Backoff_Then_Retries()
    {
        var time = Clock();
        var edits = new List<string>();
        var attempts = 0;

        var sink = new SlackResponseSink(
            postInitial: OkPost(new List<string>()),
            edit: (_, body, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw RateLimited(retryAfter: null);
                }
                edits.Add(body);
                return Task.CompletedTask;
            },
            timeProvider: time);

        await sink.BeginAsync(CancellationToken.None);

        var update = sink.UpdateAsync("hi", CancellationToken.None);
        while (!update.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(250));
        }
        await update;

        attempts.ShouldBe(2);
        edits.ShouldHaveSingleItem();
        edits[0].ShouldBe("hi" + Cursor);
    }

    [Fact]
    public async Task Begin_429_Backs_Off_Then_Retries_So_Placeholder_Lands()
    {
        var time = Clock();
        var posts = new List<string>();
        var attempts = 0;

        var sink = new SlackResponseSink(
            postInitial: (body, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw RateLimited(TimeSpan.FromSeconds(2));
                }
                posts.Add(body);
                return Task.FromResult(Handle);
            },
            edit: (_, _, _) => Task.CompletedTask,
            timeProvider: time);

        var begin = sink.BeginAsync(CancellationToken.None);
        while (!begin.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(500));
        }
        await begin;

        attempts.ShouldBe(2);
        posts.ShouldHaveSingleItem();
        posts[0].ShouldBe(Cursor);
    }

    [Fact]
    public async Task Complete_429_Backs_Off_Then_Retries_So_Terminal_Lands()
    {
        var time = Clock();
        var edits = new List<string>();
        var attempts = 0;

        var sink = new SlackResponseSink(
            postInitial: OkPost(new List<string>()),
            edit: (_, body, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw RateLimited(TimeSpan.FromSeconds(1));
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

        var sink = new SlackResponseSink(
            postInitial: OkPost(new List<string>()),
            edit: (_, body, _) =>
            {
                if (body.Contains("drop", StringComparison.Ordinal))
                {
                    // Non-rate-limit transport error on an interim edit.
                    throw new HttpRequestException("Slack returned 500");
                }
                edits.Add(body);
                return Task.CompletedTask;
            },
            timeProvider: time);

        await sink.BeginAsync(CancellationToken.None);

        await Should.NotThrowAsync(() => sink.UpdateAsync("drop-me", CancellationToken.None));

        await sink.UpdateAsync("kept", CancellationToken.None);
        await sink.CompleteAsync("final", Array.Empty<Citation>(), CancellationToken.None);

        // No corruption: the dropped interim leaves no partial edit, and later
        // renders are coherent.
        edits.ShouldBe(["kept" + Cursor, "final"]);
    }

    [Fact]
    public async Task Driven_Through_Real_StreamPump_429_On_Terminal_Still_Lands_And_Body_Is_Monotonic()
    {
        var time = Clock();
        var edits = new List<string>();
        var posts = new List<string>();
        var terminalAttempts = 0;

        var sink = new SlackResponseSink(
            postInitial: OkPost(posts),
            edit: (_, body, _) =>
            {
                // Fail the first terminal edit with a 429; interim edits pass.
                var isTerminal = !body.EndsWith(Cursor, StringComparison.Ordinal);
                if (isTerminal)
                {
                    terminalAttempts++;
                    if (terminalAttempts == 1)
                    {
                        throw RateLimited(TimeSpan.FromSeconds(1));
                    }
                }
                edits.Add(body);
                return Task.CompletedTask;
            },
            timeProvider: time);

        var pump = new StreamPump(new StreamPumpOptions
        {
            EditInterval = TimeSpan.FromMilliseconds(10),
            TimeProvider = time,
        });

        var response = new ChatResponse(Tokens("alpha ", "beta ", "gamma"));

        var run = pump.RunAsync(response, sink, CancellationToken.None);
        while (!run.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(250));
        }
        await run;

        posts.ShouldHaveSingleItem();
        terminalAttempts.ShouldBe(2); // 429 then retry.

        // Cumulative body is monotonic: each rendered body (minus the cursor) is
        // a prefix-growing snapshot, and the terminal carries the full text.
        var lengths = edits.Select(e => e.TrimEnd(Cursor[0]).Length).ToList();
        for (var i = 1; i < lengths.Count; i++)
        {
            lengths[i].ShouldBeGreaterThanOrEqualTo(lengths[i - 1]);
        }
        edits[^1].ShouldBe("alpha beta gamma");
    }

    private static async IAsyncEnumerable<string> Tokens(params string[] tokens)
    {
        foreach (var t in tokens)
        {
            yield return t;
            await Task.Yield();
        }
    }
}
