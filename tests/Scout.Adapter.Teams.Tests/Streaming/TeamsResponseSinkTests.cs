using Microsoft.Bot.Schema;
using Scout.Adapter.Teams.Streaming;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams.Tests.Streaming;

public sealed class TeamsResponseSinkTests
{
    private static TeamsResponseSink Sink(RecordingSender sender, bool streamingSupported = true)
        => new(sender, streamingSupported: streamingSupported);

    [Fact]
    public void Capabilities_Drive_Throttled_Interim_Cumulative_Plus_Final_Citations()
    {
        var caps = Sink(new RecordingSender()).Capabilities;

        // FineGrainedEdit true => the pump SENDS interim cumulative updates.
        caps.FineGrainedEdit.ShouldBeTrue();
        // Citations only on the terminal message.
        caps.SupportsInterimCitations.ShouldBeFalse();
        // ~1s floor throttles interim activities to Teams' rate.
        caps.EditIntervalFloor.ShouldBe(TimeSpan.FromSeconds(1));
        // ~2min cap folds with the pump's OverallTimeout (min wins).
        caps.MaxResponseDuration.ShouldBe(TimeSpan.FromSeconds(120));
        caps.MaxBodyLength.ShouldBe(TeamsResponseSink.MaxTeamsBodyLength);
    }

    [Fact]
    public async Task Begin_Sends_Informative_Typing_Sequence_One_And_Captures_StreamId()
    {
        var sender = new RecordingSender((_, i) => i == 0 ? "stream-42" : null);
        var sink = Sink(sender);

        await sink.BeginAsync(CancellationToken.None);

        sender.Sent.ShouldHaveSingleItem();
        var begin = sender.Sent[0];
        begin.Type.ShouldBe(ActivityTypes.Typing);
        RecordingSender.StreamType(begin).ShouldBe("informative");
        RecordingSender.StreamSequence(begin).ShouldBe(1);
        // First activity carries no streamId (it defines it).
        RecordingSender.StreamId(begin).ShouldBeNull();
    }

    [Fact]
    public async Task Updates_Carry_Full_Cumulative_Text_Monotonic_Sequence_And_StreamId()
    {
        var sender = new RecordingSender((_, i) => i == 0 ? "stream-42" : null);
        var sink = Sink(sender);

        await sink.BeginAsync(CancellationToken.None);
        await sink.UpdateAsync("Hello", CancellationToken.None);
        await sink.UpdateAsync("Hello world", CancellationToken.None);
        await sink.UpdateAsync("Hello world, full answer", CancellationToken.None);

        // begin + 3 streaming activities.
        sender.Sent.Count.ShouldBe(4);

        var streaming = sender.Sent.Skip(1).ToList();
        streaming.ShouldAllBe(a => a.Type == ActivityTypes.Typing);
        streaming.ShouldAllBe(a => RecordingSender.StreamType(a) == "streaming");

        // FULL cumulative text on each interim, never a delta.
        streaming[0].Text.ShouldBe("Hello");
        streaming[1].Text.ShouldBe("Hello world");
        streaming[2].Text.ShouldBe("Hello world, full answer");

        // Monotonic, strictly increasing sequence starting after the informative 1.
        var sequences = sender.Sent.Select(a => RecordingSender.StreamSequence(a)!.Value).ToList();
        sequences.ShouldBe([1, 2, 3, 4]);

        // Every later activity echoes the stream id from Begin.
        RecordingSender.StreamId(streaming[0]).ShouldBe("stream-42");
        RecordingSender.StreamId(streaming[2]).ShouldBe("stream-42");
    }

    [Fact]
    public async Task Complete_Sends_Final_Message_With_Citations_Only_On_Final()
    {
        var sender = new RecordingSender((_, i) => i == 0 ? "stream-42" : null);
        var sink = Sink(sender);

        await sink.BeginAsync(CancellationToken.None);
        await sink.UpdateAsync("partial", CancellationToken.None);
        await sink.CompleteAsync(
            "the final answer",
            new[] { new Citation("src/A.cs:1") { Path = "src/A.cs", Line = 1 } },
            CancellationToken.None);

        // No interim activity carried citations.
        foreach (var interim in sender.Sent.Take(sender.Sent.Count - 1))
        {
            (interim.Attachments is null || interim.Attachments.Count == 0).ShouldBeTrue();
            HasCitationEntity(interim).ShouldBeFalse();
        }

        var final = sender.Sent[^1];
        final.Type.ShouldBe(ActivityTypes.Message);
        RecordingSender.StreamType(final).ShouldBe("final");
        final.Text.ShouldBe("the final answer");
        HasCitationEntity(final).ShouldBeTrue();
        final.Attachments.ShouldNotBeNull();
        final.Attachments!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Complete_Without_Citations_Has_No_Attachments()
    {
        var sender = new RecordingSender((_, i) => i == 0 ? "stream-42" : null);
        var sink = Sink(sender);

        await sink.BeginAsync(CancellationToken.None);
        await sink.CompleteAsync("done", Array.Empty<Citation>(), CancellationToken.None);

        var final = sender.Sent[^1];
        final.Text.ShouldBe("done");
        // CreateMessageActivity seeds an empty Attachments list; no citation card.
        (final.Attachments is null || final.Attachments.Count == 0).ShouldBeTrue();
        HasCitationEntity(final).ShouldBeFalse();
    }

    [Fact]
    public async Task Fail_Sends_Final_Message_With_Partial_And_Error_Footer()
    {
        var sender = new RecordingSender((_, i) => i == 0 ? "stream-42" : null);
        var sink = Sink(sender);

        await sink.BeginAsync(CancellationToken.None);
        await sink.UpdateAsync("partial text", CancellationToken.None);
        await sink.FailAsync("partial text", "boom", CancellationToken.None);

        var final = sender.Sent[^1];
        final.Type.ShouldBe(ActivityTypes.Message);
        RecordingSender.StreamType(final).ShouldBe("final");
        final.Text.ShouldStartWith("partial text");
        final.Text.ShouldContain("response interrupted");
    }

    [Fact]
    public async Task Group_Chat_Is_Final_Only_No_Typing_Activities()
    {
        var sender = new RecordingSender();
        var sink = Sink(sender, streamingSupported: false);

        await sink.BeginAsync(CancellationToken.None);
        await sink.UpdateAsync("interim 1", CancellationToken.None);
        await sink.UpdateAsync("interim 2", CancellationToken.None);
        await sink.CompleteAsync("final answer", Array.Empty<Citation>(), CancellationToken.None);

        // Only the terminal message activity hit the wire.
        sender.Sent.ShouldHaveSingleItem();
        sender.Sent[0].Type.ShouldBe(ActivityTypes.Message);
        sender.Sent[0].Text.ShouldBe("final answer");
    }

    [Fact]
    public async Task Lifecycle_Rejects_Update_Before_Begin()
    {
        var sink = Sink(new RecordingSender());
        await Should.ThrowAsync<InvalidOperationException>(
            () => sink.UpdateAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_Rejects_Double_Begin()
    {
        var sink = Sink(new RecordingSender());
        await sink.BeginAsync(CancellationToken.None);
        await Should.ThrowAsync<InvalidOperationException>(
            () => sink.BeginAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_Rejects_Update_After_Terminal()
    {
        var sink = Sink(new RecordingSender());
        await sink.BeginAsync(CancellationToken.None);
        await sink.CompleteAsync("done", Array.Empty<Citation>(), CancellationToken.None);
        await Should.ThrowAsync<InvalidOperationException>(
            () => sink.UpdateAsync("more", CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_Rejects_Second_Terminal()
    {
        var sink = Sink(new RecordingSender());
        await sink.BeginAsync(CancellationToken.None);
        await sink.CompleteAsync("done", Array.Empty<Citation>(), CancellationToken.None);
        await Should.ThrowAsync<InvalidOperationException>(
            () => sink.FailAsync("x", "y", CancellationToken.None));
    }

    private static bool HasCitationEntity(Activity a) =>
        a.Entities?.Any(e => e.Type == "https://schema.org/Message") == true;
}
