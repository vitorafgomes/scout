using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Scout.Adapter.Discord.Streaming;

namespace Scout.Adapter.Discord.Tests.Streaming;

public class EmbedEditThrottlerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);
    private const string Cursor = "▌";

    private static (EmbedEditThrottler Throttler, List<string> Edits, FakeTimeProvider Time)
        Create(
            TimeSpan? interval = null,
            int? maxLength = null,
            string? placeholder = null,
            Func<string, CancellationToken, Task>? editOverride = null)
    {
        var edits = new List<string>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 23, 0, 0, 0, TimeSpan.Zero));

        Func<string, CancellationToken, Task> editDelegate = editOverride
            ?? ((body, _) =>
            {
                edits.Add(body);
                return Task.CompletedTask;
            });

        var opts = new EmbedEditThrottlerOptions
        {
            EditAsync = editDelegate,
            EditInterval = interval ?? Interval,
            MaxLength = maxLength ?? 4096,
            EmptyStreamPlaceholder = placeholder ?? "(no response from model)",
            TimeProvider = time,
        };

        return (new EmbedEditThrottler(opts), edits, time);
    }

    [Fact]
    public async Task AppendUnderInterval_DoesNotEdit()
    {
        var (throttler, edits, time) = Create();
        await using var _ = throttler;

        await throttler.AppendAsync("A", CancellationToken.None);
        // First chunk triggers an edit immediately (ShouldEditNow returns true
        // when LastEditAt is null). Reset and append within interval.
        edits.Clear();

        time.Advance(TimeSpan.FromMilliseconds(100));
        await throttler.AppendAsync("B", CancellationToken.None);

        edits.ShouldBeEmpty();
    }

    [Fact]
    public async Task AppendAcrossInterval_EditsOnce()
    {
        var (throttler, edits, time) = Create();
        await using var _ = throttler;

        await throttler.AppendAsync("A", CancellationToken.None);
        edits.Clear();

        time.Advance(Interval + TimeSpan.FromMilliseconds(10));
        await throttler.AppendAsync("B", CancellationToken.None);

        edits.ShouldHaveSingleItem();
        edits[0].ShouldBe("AB" + Cursor);
    }

    [Fact]
    public async Task RapidChunksCoalesce_SingleEditPerWindow()
    {
        var (throttler, edits, time) = Create();
        await using var _ = throttler;

        // First append fires the initial edit; clear to measure coalescing.
        await throttler.AppendAsync("1", CancellationToken.None);
        edits.Clear();

        await throttler.AppendAsync("2", CancellationToken.None);
        await throttler.AppendAsync("3", CancellationToken.None);
        await throttler.AppendAsync("4", CancellationToken.None);
        await throttler.AppendAsync("5", CancellationToken.None);

        edits.ShouldBeEmpty();

        time.Advance(Interval + TimeSpan.FromMilliseconds(1));
        await throttler.AppendAsync("6", CancellationToken.None);

        edits.ShouldHaveSingleItem();
        edits[0].ShouldBe("123456" + Cursor);
    }

    [Fact]
    public async Task OrderPreservedAcrossWindows()
    {
        var (throttler, edits, time) = Create();
        await using var _ = throttler;

        await throttler.AppendAsync("A", CancellationToken.None);
        time.Advance(Interval + TimeSpan.FromMilliseconds(1));
        await throttler.AppendAsync("B", CancellationToken.None);
        time.Advance(Interval + TimeSpan.FromMilliseconds(1));
        await throttler.AppendAsync("", CancellationToken.None);

        edits.Count.ShouldBeGreaterThanOrEqualTo(2);
        edits[^1].ShouldBe("AB" + Cursor);
    }

    [Fact]
    public async Task FinalizeWithoutCursor()
    {
        var (throttler, edits, time) = Create();
        await using var _ = throttler;

        await throttler.AppendAsync("hello", CancellationToken.None);
        time.Advance(Interval + TimeSpan.FromMilliseconds(1));
        await throttler.AppendAsync(" world", CancellationToken.None);

        await throttler.FinalizeAsync(CancellationToken.None);

        edits[^1].ShouldBe("hello world");
        edits[^1].ShouldNotContain(Cursor);
    }

    [Fact]
    public async Task FinalizeEmpty_UsesPlaceholder()
    {
        var (throttler, edits, _) = Create(placeholder: "(empty)");
        await using var _disp = throttler;

        await throttler.FinalizeAsync(CancellationToken.None);

        edits.ShouldHaveSingleItem();
        edits[0].ShouldBe("(empty)");
    }

    [Fact]
    public async Task Truncation_TailKept()
    {
        const int max = 64;
        var (throttler, edits, time) = Create(maxLength: max);
        await using var _ = throttler;

        var longText = new string('a', 200);
        await throttler.AppendAsync(longText, CancellationToken.None);

        // First edit fires immediately on initial append.
        edits.ShouldNotBeEmpty();
        var body = edits[^1];
        body.Length.ShouldBeLessThanOrEqualTo(max);
        body.ShouldStartWith("... (truncated)\n\n");
        body.ShouldEndWith("a" + Cursor);
    }

    [Fact]
    public async Task FinalizeWithError_AppendsFooter()
    {
        var (throttler, edits, _) = Create();
        await using var _ = throttler;

        await throttler.AppendAsync("partial", CancellationToken.None);
        await throttler.FinalizeWithErrorAsync("\n\n(error: stream broke)", CancellationToken.None);

        edits[^1].ShouldEndWith("(error: stream broke)");
        edits[^1].ShouldStartWith("partial");
    }

    [Fact]
    public async Task RateLimitRetry_RespectsRetryAfter()
    {
        var attempts = 0;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 23, 0, 0, 0, TimeSpan.Zero));
        var captured = new List<string>();

        var opts = new EmbedEditThrottlerOptions
        {
            EditAsync = (body, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new FakeRateLimitedException(TimeSpan.FromMilliseconds(200));
                }

                captured.Add(body);
                return Task.CompletedTask;
            },
            TimeProvider = time,
        };

        await using var throttler = new EmbedEditThrottler(opts);

        var sw = Stopwatch.StartNew();
        var appendTask = throttler.AppendAsync("payload", CancellationToken.None);

        // Allow the first edit attempt to throw and the retry to register the
        // Task.Delay against the FakeTimeProvider before we advance time.
        while (!appendTask.IsCompleted && sw.ElapsedMilliseconds < 1000)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(50));
        }

        await appendTask;

        attempts.ShouldBe(2);
        captured.ShouldHaveSingleItem();
        captured[0].ShouldBe("payload" + Cursor);
    }

    [Fact]
    public async Task RateLimitNoRetryAfter_DropsEditButKeepsBuffer()
    {
        var attempts = 0;
        var seenBodies = new List<string>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 23, 0, 0, 0, TimeSpan.Zero));

        var opts = new EmbedEditThrottlerOptions
        {
            EditAsync = (body, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    // No RetryAfter property — throttler must drop the edit.
                    throw new InvalidOperationException("transient boom");
                }

                seenBodies.Add(body);
                return Task.CompletedTask;
            },
            TimeProvider = time,
        };

        await using var throttler = new EmbedEditThrottler(opts);

        await throttler.AppendAsync("hello", CancellationToken.None);
        attempts.ShouldBe(1);
        seenBodies.ShouldBeEmpty();

        // Buffer must survive so the next tick re-sends the full content.
        throttler.Snapshot().BufferLength.ShouldBe(5);

        time.Advance(Interval + TimeSpan.FromMilliseconds(1));
        await throttler.AppendAsync(" world", CancellationToken.None);

        seenBodies.ShouldHaveSingleItem();
        seenBodies[0].ShouldBe("hello world" + Cursor);
    }

    [Fact]
    public async Task Dispose_CancelsPendingEdit_NoThrow()
    {
        var (throttler, _, _) = Create();
        await throttler.AppendAsync("x", CancellationToken.None);

        Func<Task> act = async () => await throttler.DisposeAsync();
        await act.ShouldNotThrowAsync();

        // Idempotent.
        Func<Task> again = async () => await throttler.DisposeAsync();
        await again.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task Snapshot_ReturnsBufferLengthAndEditCount()
    {
        var (throttler, _, time) = Create();
        await using var _ = throttler;

        await throttler.AppendAsync("abc", CancellationToken.None);
        var s1 = throttler.Snapshot();
        s1.EditCount.ShouldBe(1);
        s1.BufferLength.ShouldBe(3);
        s1.LastEditAt.ShouldNotBeNull();

        time.Advance(Interval + TimeSpan.FromMilliseconds(1));
        await throttler.AppendAsync("de", CancellationToken.None);

        var s2 = throttler.Snapshot();
        s2.EditCount.ShouldBe(2);
        s2.BufferLength.ShouldBe(5);
    }

    // Test-only exception that mimics Discord.Net's RateLimitedException shape:
    // a public RetryAfter property and a message containing "rate limit". The
    // throttler must recognise this via duck typing alone.
    private sealed class FakeRateLimitedException : Exception
    {
        public FakeRateLimitedException(TimeSpan retryAfter)
            : base("rate limited")
        {
            RetryAfter = retryAfter;
        }

        public TimeSpan RetryAfter { get; }
    }
}
