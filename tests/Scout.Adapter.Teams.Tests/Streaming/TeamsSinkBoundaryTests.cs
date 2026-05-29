using Microsoft.Bot.Schema;
using Microsoft.Extensions.Time.Testing;
using Scout.Adapter.Teams.Streaming;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams.Tests.Streaming;

/// <summary>
/// Exercises the Teams sink's OWN transport resilience directly with an injected
/// sender and a FakeTimeProvider. Hermetic — no network, no Azure. Rate limits
/// (429) and the 403 ContentStreamNotAllowed terminal-cancel are the sink's
/// concern, not the pump's.
/// </summary>
public sealed class TeamsSinkBoundaryTests
{
    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Update_429_Backs_Off_On_FakeClock_Then_Retries_Once()
    {
        var time = Clock();
        var attempts = 0;
        var delivered = new List<string?>();

        var sender = new RecordingSender((activity, _) =>
        {
            attempts++;
            // First streaming attempt (index 1 overall) is rate-limited once.
            if (attempts == 2)
            {
                throw new TeamsTransportException(429, message: "Too Many Requests");
            }
            delivered.Add(activity.Text);
            return attempts == 1 ? "stream-1" : null;
        });

        var sink = new TeamsResponseSink(sender, timeProvider: time);
        await sink.BeginAsync(CancellationToken.None);

        var update = sink.UpdateAsync("cumulative body", CancellationToken.None);
        while (!update.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(250));
        }
        await update;

        // begin attempt + first (429) + retry = 3 send attempts.
        attempts.ShouldBe(3);
        // The retried update carried the full cumulative body.
        delivered.ShouldContain("cumulative body");
    }

    [Fact]
    public async Task Complete_429_Backs_Off_Then_Retries_So_Terminal_Lands()
    {
        var time = Clock();
        var attempts = 0;
        Activity? landed = null;

        var sender = new RecordingSender((activity, _) =>
        {
            attempts++;
            if (attempts == 2)
            {
                throw new TeamsTransportException(429);
            }
            if (activity.Type == ActivityTypes.Message)
            {
                landed = activity;
            }
            return attempts == 1 ? "stream-1" : null;
        });

        var sink = new TeamsResponseSink(sender, timeProvider: time);
        await sink.BeginAsync(CancellationToken.None);

        var complete = sink.CompleteAsync("the answer", Array.Empty<Citation>(), CancellationToken.None);
        while (!complete.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(250));
        }
        await complete;

        attempts.ShouldBe(3);
        landed.ShouldNotBeNull();
        landed!.Text.ShouldBe("the answer");
    }

    [Fact]
    public async Task Update_403_ContentStreamNotAllowed_Cancels_Stream_Terminal_Still_Lands()
    {
        var sender = new RecordingSender((activity, i) =>
        {
            // Begin (index 0) succeeds; first streaming activity (index 1) is
            // refused with 403 ContentStreamNotAllowed.
            if (i == 1)
            {
                throw new TeamsTransportException(
                    403, TeamsTransportException.ContentStreamNotAllowed);
            }
            return i == 0 ? "stream-1" : null;
        });

        var sink = new TeamsResponseSink(sender);
        await sink.BeginAsync(CancellationToken.None);

        // The refused interim must NOT throw out of the sink.
        await Should.NotThrowAsync(() => sink.UpdateAsync("body so far", CancellationToken.None));
        // Subsequent interims are silently dropped (stream canceled).
        await sink.UpdateAsync("more body", CancellationToken.None);

        // The terminal message still lands as a single final activity.
        await sink.CompleteAsync("final answer", Array.Empty<Citation>(), CancellationToken.None);

        var final = sender.Sent[^1];
        final.Type.ShouldBe(ActivityTypes.Message);
        final.Text.ShouldBe("final answer");
        // The stream was canceled (403), so the terminal closes nothing: a plain
        // message with NO streamId-less `final` streaminfo entity.
        RecordingSender.HasStreamInfoEntity(final).ShouldBeFalse();
        RecordingSender.StreamType(final).ShouldBeNull();
        RecordingSender.StreamId(final).ShouldBeNull();
    }

    [Fact]
    public async Task Begin_403_ContentStreamNotAllowed_Downgrades_To_Final_Only()
    {
        var sender = new RecordingSender((_, i) =>
        {
            if (i == 0)
            {
                throw new TeamsTransportException(
                    403, TeamsTransportException.ContentStreamNotAllowed);
            }
            return null;
        });

        var sink = new TeamsResponseSink(sender);

        // Begin must not throw on a 403 ContentStreamNotAllowed; it downgrades.
        await Should.NotThrowAsync(() => sink.BeginAsync(CancellationToken.None));
        await sink.UpdateAsync("interim", CancellationToken.None);
        await sink.CompleteAsync("answer", Array.Empty<Citation>(), CancellationToken.None);

        // Only the terminal message reached the wire after the refused begin.
        var messages = sender.Sent.Where(a => a.Type == ActivityTypes.Message).ToList();
        messages.ShouldHaveSingleItem();
        messages[0].Text.ShouldBe("answer");
        // Streaming was never opened, so the terminal is a PLAIN message: no
        // streamId-less `final` streaminfo entity.
        RecordingSender.HasStreamInfoEntity(messages[0]).ShouldBeFalse();
        RecordingSender.StreamType(messages[0]).ShouldBeNull();
        RecordingSender.StreamId(messages[0]).ShouldBeNull();
    }

    [Fact]
    public async Task Update_5xx_Is_Dropped_Terminal_Still_Coherent()
    {
        var sender = new RecordingSender((activity, _) =>
        {
            if (activity.Text?.Contains("drop", StringComparison.Ordinal) == true)
            {
                throw new TeamsTransportException(500, message: "server error");
            }
            return null;
        });

        var sink = new TeamsResponseSink(sender);
        await sink.BeginAsync(CancellationToken.None);

        await Should.NotThrowAsync(() => sink.UpdateAsync("drop-me", CancellationToken.None));
        await sink.UpdateAsync("kept", CancellationToken.None);
        await sink.CompleteAsync("final", Array.Empty<Citation>(), CancellationToken.None);

        var texts = sender.Sent.Select(a => a.Text).ToList();
        texts.ShouldContain("kept");
        texts.ShouldContain("final");
        texts.ShouldNotContain("drop-me");
    }
}
