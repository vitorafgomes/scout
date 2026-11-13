using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;

namespace Scout.Conversation.Tests;

/// <summary>
/// A voice message that arrives while no speech model is installed tells the desktop app, with the chat platform
/// only, exactly when the conductor answers with the no-model reply, and never otherwise.
/// </summary>
public sealed class ConversationConductorVoiceNoticeTests
{
    private static readonly RenderCapabilities FineGrained = new() { FineGrainedEdit = true, MaxBodyLength = 4096 };

    private sealed class RecordingVoiceNotice(bool throws = false) : IVoiceNoticeSignal
    {
        public List<string> Platforms { get; } = [];

        public void NoSpeechModel(string platform)
        {
            Platforms.Add(platform);
            if (throws)
            {
                throw new IOException("stdout closed");
            }
        }
    }

    private static StreamPump Pump() => new(new StreamPumpOptions
    {
        TimeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero)),
    });

    private static async IAsyncEnumerable<string> Tokens(string answer)
    {
        await Task.Yield();
        yield return answer;
    }

    private static IConversationRouter Router()
    {
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>()).Returns(_ => new ChatResponse(Tokens("It is green.")));
        return router;
    }

    private static IAudioTranscriber Transcriber(Func<string> transcribe)
    {
        var transcriber = Substitute.For<IAudioTranscriber>();
        transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => transcribe());
        return transcriber;
    }

    private static ConversationConductor Conductor(
        IAudioTranscriber? transcriber,
        IVoiceNoticeSignal notice,
        AccessOptions? access = null,
        RecordingLogger<ConversationConductor>? logger = null)
        => new(Router(), Pump(), logger, new AccessPolicy(access ?? new AccessOptions()), transcriber, voiceNotice: notice);

    private static ChatMessage Voice(
        string? origin = "discord:42:7",
        bool localOwner = false,
        long sizeBytes = 2048,
        Func<Stream>? open = null,
        string userId = "111")
        => new(
            userId,
            "thread-7",
            string.Empty,
            new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero),
            new AudioAttachment(
                "audio/ogg",
                sizeBytes,
                TimeSpan.FromSeconds(4),
                _ => Task.FromResult(open?.Invoke() ?? new MemoryStream(new byte[2048]))))
        {
            OriginReference = origin,
            IsLocalOwner = localOwner,
        };

    public static TheoryData<string, ChatMessage, string> NoModel => new()
    {
        { "a Discord voice message", Voice(), "discord" },
        { "the owner's voice message on this computer", Voice(origin: null, localOwner: true), "desktop" },
        { "a voice message whose conversation is unknown", Voice(origin: null), "chat" },
    };

    [Theory]
    [MemberData(nameof(NoModel))]
    public async Task Without_A_Speech_Model_The_No_Model_Reply_And_One_Notice_With_The_Platform_Only(
        string _, ChatMessage message, string platform)
    {
        var notice = new RecordingVoiceNotice();
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(null, notice).HandleAsync(message, sink, CancellationToken.None);

        sink.FinalText.ShouldBe(ConversationConductor.NoTranscriberReply);
        notice.Platforms.ShouldBe([platform]);
    }

    public static TheoryData<string, Func<IAudioTranscriber>, ChatMessage, string> WithModel => new()
    {
        { "transcribed", () => Transcriber(() => "is prod green"), Voice(), "> (voice) is prod green\n\nIt is green." },
        { "too large", () => Transcriber(() => "unused"), Voice(sizeBytes: ConversationConductor.MaxAudioBytes + 1), ConversationConductor.AudioTooLargeReply },
        { "not downloaded", () => Transcriber(() => "unused"), Voice(open: () => throw new HttpRequestException("reset")), ConversationConductor.AudioDownloadFailedReply },
        { "the model busy", () => Transcriber(() => throw new TranscriptionBusyException("held")), Voice(), ConversationConductor.TranscriberBusyReply },
        { "the speech runtime missing", () => Transcriber(() => throw new TranscriptionUnavailableException("no runtime", new DllNotFoundException("libgomp.so.1"))), Voice(), "Scout cannot transcribe on this computer: the speech runtime did not load. The reason is in Scout's logs." },
        { "the model failing", () => Transcriber(() => throw new InvalidOperationException("bad model")), Voice(), ConversationConductor.TranscriptionFailedReply },
    };

    [Theory]
    [MemberData(nameof(WithModel))]
    public async Task With_A_Speech_Model_A_Voice_Message_Never_Sends_The_Notice(
        string _, Func<IAudioTranscriber> transcriber, ChatMessage message, string reply)
    {
        var notice = new RecordingVoiceNotice();
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(transcriber(), notice).HandleAsync(message, sink, CancellationToken.None);

        sink.FinalText.ShouldBe(reply);
        notice.Platforms.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Typed_Message_Without_A_Speech_Model_Never_Sends_The_Notice()
    {
        var notice = new RecordingVoiceNotice();
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(null, notice).HandleAsync(new ChatMessage("111", "thread-7", "is prod green"), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("It is green.");
        notice.Platforms.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Refused_Senders_Voice_Message_Without_A_Speech_Model_Never_Sends_The_Notice()
    {
        var notice = new RecordingVoiceNotice();
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(null, notice, new AccessOptions { AllowedUserIds = ["owner"] })
            .HandleAsync(Voice(userId: "stranger"), sink, CancellationToken.None);

        sink.FinalText.ShouldNotBe(ConversationConductor.NoTranscriberReply);
        notice.Platforms.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Notice_That_Cannot_Be_Sent_Leaves_The_No_Model_Reply_As_The_Only_Answer_And_Says_So_In_The_Log()
    {
        var notice = new RecordingVoiceNotice(throws: true);
        var sink = new RecordingResponseSink(FineGrained);
        var logger = new RecordingLogger<ConversationConductor>();

        await Conductor(null, notice, logger: logger).HandleAsync(Voice(), sink, CancellationToken.None);

        sink.FinalText.ShouldBe(ConversationConductor.NoTranscriberReply);
        sink.TerminalCount.ShouldBe(1);
        sink.Failed.ShouldBeFalse();
        logger.Entries.ShouldContain(
            (LogLevel.Warning, "Could not tell the desktop app that a voice message needs a speech model."));
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Error);
    }
}
