using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;

namespace Scout.Conversation.Tests;

public sealed class ConversationConductorLocalOwnerTests
{
    private const string NoAllowedUsersReply =
        "Scout is not set up for anyone yet. If you are the owner, open the Scout desktop app, go to Settings, "
        + "choose Pair from chat and send the 6-digit code here in a direct message.";
    private const string Code = "482913";

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly RenderCapabilities FineGrained = new() { FineGrainedEdit = true, MaxBodyLength = 4096 };

    private sealed class RecordingPairingSignal : IPairingSignal
    {
        public List<string> UserIds { get; } = [];
        public int BurnedCount { get; private set; }

        public void Paired(string userId) => UserIds.Add(userId);

        public void Burned() => BurnedCount++;
    }

    private static async IAsyncEnumerable<string> Tokens(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static IConversationRouter AnsweringRouter()
    {
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>()).Returns(_ => new ChatResponse(Tokens("hello")));
        return router;
    }

    private static ConversationConductor Conductor(
        IConversationRouter router,
        AccessOptions access,
        IAudioTranscriber? transcriber = null,
        IPairingSignal? signal = null)
    {
        var clock = new FakeTimeProvider(Now);
        return new ConversationConductor(
            router,
            new StreamPump(new StreamPumpOptions { TimeProvider = clock }),
            null,
            new AccessPolicy(access),
            transcriber,
            signal,
            clock);
    }

    private static ChatMessage Local(string text, AudioAttachment? audio = null)
        => new("local-owner", "local", text, Now, audio) { IsLocalOwner = true };

    [Fact]
    public async Task A_Local_Owner_Message_Is_Answered_With_An_Empty_List_And_RequireAllowList()
    {
        var router = AnsweringRouter();
        var conductor = Conductor(router, new AccessOptions { RequireAllowList = true });
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(Local("what failed in the nightly build?"), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("hello");
        router.Received(1).Route(Arg.Is<ChatMessage>(m => m.Text == "what failed in the nightly build?"));
    }

    [Fact]
    public async Task A_Local_Owner_Message_Is_Answered_When_The_List_Names_Only_Chat_Users()
    {
        var router = AnsweringRouter();
        var conductor = Conductor(router, new AccessOptions { AllowedUserIds = ["111"], RequireAllowList = true });
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(Local("hi"), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("hello");
    }

    [Fact]
    public async Task A_Chat_Message_With_The_Local_Owner_User_Id_Is_Still_Refused()
    {
        var router = AnsweringRouter();
        var conductor = Conductor(router, new AccessOptions { RequireAllowList = true });
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(new ChatMessage("local-owner", "local", "hi"), sink, CancellationToken.None);

        sink.FinalText.ShouldBe(NoAllowedUsersReply);
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task A_Local_Owner_Message_That_Looks_Like_A_Pairing_Code_Is_Routed_And_Never_Pairs_Or_Burns()
    {
        var router = AnsweringRouter();
        var signal = new RecordingPairingSignal();
        var conductor = Conductor(
            router,
            new AccessOptions { RequireAllowList = true, PairingCode = Code, PairingExpiresAt = Now.AddMinutes(10) },
            signal: signal);
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(Local("pair " + Code), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("hello");
        signal.UserIds.ShouldBeEmpty();
        signal.BurnedCount.ShouldBe(0);
        router.Received(1).Route(Arg.Is<ChatMessage>(m => m.Text == "pair " + Code));
    }

    [Fact]
    public async Task A_Local_Owner_Voice_Message_Is_Transcribed_And_Stays_Local_Owner()
    {
        var router = AnsweringRouter();
        var transcriber = Substitute.For<IAudioTranscriber>();
        transcriber.TranscribeAsync(Arg.Any<Stream>(), "audio/wav", Arg.Any<CancellationToken>())
            .Returns("is the billing service green");
        var conductor = Conductor(router, new AccessOptions { RequireAllowList = true }, transcriber);
        var sink = new RecordingResponseSink(FineGrained);
        var audio = new AudioAttachment(
            "audio/wav", 64, TimeSpan.FromSeconds(2), _ => Task.FromResult<Stream>(new MemoryStream(new byte[64])));

        await conductor.HandleAsync(Local(string.Empty, audio), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("> (voice) is the billing service green\n\nhello");
        router.Received(1).Route(Arg.Is<ChatMessage>(m => m.IsLocalOwner && m.Text == "is the billing service green"));
    }

    [Fact]
    public async Task A_Transcribed_Voice_Message_Keeps_The_Screenshot_Attached_To_It()
    {
        byte[] screenshot = [0xFF, 0xD8, 0xFF, 0xE0, 0x07];
        var router = AnsweringRouter();
        var transcriber = Substitute.For<IAudioTranscriber>();
        transcriber.TranscribeAsync(Arg.Any<Stream>(), "audio/wav", Arg.Any<CancellationToken>())
            .Returns("what is this error");
        var conductor = Conductor(router, new AccessOptions { RequireAllowList = true }, transcriber);
        var audio = new AudioAttachment(
            "audio/wav", 64, TimeSpan.FromSeconds(2), _ => Task.FromResult<Stream>(new MemoryStream(new byte[64])));
        var message = Local(string.Empty, audio) with { Image = new ImageAttachment("image/jpeg", screenshot) };

        await conductor.HandleAsync(message, new RecordingResponseSink(FineGrained), CancellationToken.None);

        var routed = router.ReceivedCalls().ShouldHaveSingleItem().GetArguments()[0].ShouldBeOfType<ChatMessage>();
        routed.Text.ShouldBe("what is this error");
        var image = routed.Image.ShouldNotBeNull();
        image.ContentType.ShouldBe("image/jpeg");
        image.Bytes.ToArray().ShouldBe(screenshot);
    }
}
