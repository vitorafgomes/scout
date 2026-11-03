using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;

namespace Scout.Conversation.Tests;

public sealed class ConversationConductorOriginTests
{
    private static readonly RenderCapabilities FineGrained = new() { FineGrainedEdit = true, MaxBodyLength = 4096 };

    private static ConversationConductor Conductor(IConversationRouter router, IAudioTranscriber? transcriber = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        return new ConversationConductor(
            router,
            new StreamPump(new StreamPumpOptions { TimeProvider = clock }),
            null,
            new AccessPolicy(new AccessOptions()),
            transcriber,
            null,
            clock);
    }

    private static async IAsyncEnumerable<string> OriginSeenWhileStreaming(Func<Task>? midTurn = null)
    {
        await Task.Yield();
        if (midTurn is not null)
        {
            await midTurn();
        }
        yield return ChatOrigin.Current ?? "<none>";
    }

    private static IConversationRouter RouterReportingOrigin(List<string?> seenAtRoute, Func<Task>? midTurn = null)
    {
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>()).Returns(_ =>
        {
            lock (seenAtRoute)
            {
                seenAtRoute.Add(ChatOrigin.Current);
            }
            return new ChatResponse(OriginSeenWhileStreaming(midTurn));
        });
        return router;
    }

    private static ChatMessage Message(string text, string? origin)
        => new("U1", "C1", text, new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero)) { OriginReference = origin };

    [Fact]
    public async Task During_A_Turn_ChatOrigin_Current_Is_The_Message_Origin_Reference()
    {
        var seenAtRoute = new List<string?>();
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(RouterReportingOrigin(seenAtRoute))
            .HandleAsync(Message("start the weekly video", "slack:C1:1700000000.000100"), sink, CancellationToken.None);

        seenAtRoute.ShouldBe(["slack:C1:1700000000.000100"]);
        sink.FinalText.ShouldBe("slack:C1:1700000000.000100", "a function the model calls while the reply streams reads the same reference");
    }

    [Fact]
    public async Task After_A_Turn_ChatOrigin_Current_Is_Null()
    {
        await Conductor(RouterReportingOrigin([]))
            .HandleAsync(Message("start the weekly video", "discord:3333:42"), new RecordingResponseSink(FineGrained), CancellationToken.None);

        ChatOrigin.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Two_Concurrent_Turns_Each_See_Only_Their_Own_Origin_Reference()
    {
        var bothInFlight = new Barrier(2);
        Task MidTurn() => Task.Run(() => bothInFlight.SignalAndWait(TimeSpan.FromSeconds(5)).ShouldBeTrue());
        var seenAtRoute = new List<string?>();
        var conductor = Conductor(RouterReportingOrigin(seenAtRoute, MidTurn));
        var discordSink = new RecordingResponseSink(FineGrained);
        var slackSink = new RecordingResponseSink(FineGrained);

        await Task.WhenAll(
            Task.Run(() => conductor.HandleAsync(Message("first", "discord:3333:42"), discordSink, CancellationToken.None)),
            Task.Run(() => conductor.HandleAsync(Message("second", "slack:C9:1.2"), slackSink, CancellationToken.None)));

        discordSink.FinalText.ShouldBe("discord:3333:42");
        slackSink.FinalText.ShouldBe("slack:C9:1.2");
        seenAtRoute.ShouldBe(["discord:3333:42", "slack:C9:1.2"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_Voice_Message_Keeps_Its_Origin_Reference_When_Rewritten_To_Its_Transcript()
    {
        var seenAtRoute = new List<string?>();
        var router = RouterReportingOrigin(seenAtRoute);
        var transcriber = Substitute.For<IAudioTranscriber>();
        transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("start the weekly video");
        var voice = new ChatMessage(
            "U1", "C1", string.Empty,
            Audio: new AudioAttachment("audio/ogg", 3, TimeSpan.FromSeconds(2), _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]))))
        { OriginReference = "discord:3333:42" };

        await Conductor(router, transcriber).HandleAsync(voice, new RecordingResponseSink(FineGrained), CancellationToken.None);

        seenAtRoute.ShouldBe(["discord:3333:42"]);
        router.Received(1).Route(Arg.Is<ChatMessage>(m => m.Text == "start the weekly video" && m.OriginReference == "discord:3333:42"));
    }
}
