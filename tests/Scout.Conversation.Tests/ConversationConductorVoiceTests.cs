using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;

namespace Scout.Conversation.Tests;

public sealed class ConversationConductorVoiceTests
{
    private const string Heard = "is the nightly build of the billing service green";

    private static readonly RenderCapabilities FineGrained = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = 4096,
    };

    private sealed class CountingDownload(Func<Stream> open)
    {
        public int Calls { get; private set; }

        public Task<Stream> OpenAsync()
        {
            Calls++;
            return Task.FromResult(open());
        }
    }

    private static StreamPump Pump() => new(new StreamPumpOptions
    {
        TimeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero)),
    });

    private static async IAsyncEnumerable<string> Tokens(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static IConversationRouter AnsweringRouter(string answer = "It is green.")
    {
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>()).Returns(_ => new ChatResponse(Tokens(answer)));
        return router;
    }

    private static IAudioTranscriber Transcriber(string heard = Heard)
    {
        var transcriber = Substitute.For<IAudioTranscriber>();
        transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(heard);
        return transcriber;
    }

    private static ConversationConductor Conductor(
        IConversationRouter router,
        IAudioTranscriber? transcriber,
        AccessOptions? access = null,
        RecordingLogger<ConversationConductor>? logger = null)
        => new(router, Pump(), logger, new AccessPolicy(access ?? new AccessOptions()), transcriber);

    private static ChatMessage Voice(
        CountingDownload download,
        long sizeBytes = 2048,
        TimeSpan? duration = null,
        string userId = "111")
        => new(
            userId,
            "thread-7",
            string.Empty,
            new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero),
            new AudioAttachment("audio/ogg", sizeBytes, duration ?? TimeSpan.FromSeconds(4), _ => download.OpenAsync()));

    private static CountingDownload Bytes(int length = 2048) => new(() => new MemoryStream(new byte[length]));

    [Fact]
    public async Task A_Voice_Message_Is_Transcribed_And_The_Transcript_Is_Routed_And_Quoted_In_The_Reply()
    {
        var router = AnsweringRouter();
        var transcribed = new List<(long Length, string ContentType)>();
        var transcriber = Substitute.For<IAudioTranscriber>();
        transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                transcribed.Add((call.Arg<Stream>().Length, call.Arg<string>()));
                return Heard;
            });
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(router, transcriber).HandleAsync(Voice(Bytes()), sink, CancellationToken.None);

        router.Received(1).Route(Arg.Is<ChatMessage>(m =>
            m.Text == Heard && m.ChannelId == "thread-7" && m.UserId == "111" && m.Audio == null));
        transcribed.ShouldBe([(2048L, "audio/ogg")]);
        sink.FinalText.ShouldBe("> (voice) " + Heard + "\n\nIt is green.");
        sink.BeginCount.ShouldBe(1);
    }

    [Fact]
    public async Task The_Quote_Holds_The_First_200_Characters_Of_The_Transcript_On_One_Line()
    {
        var heard = "line one\n" + new string('w', 300);
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(AnsweringRouter(), Transcriber(heard)).HandleAsync(Voice(Bytes()), sink, CancellationToken.None);

        var expectedQuote = ("line one " + new string('w', 300))[..200];
        sink.FinalText.ShouldBe("> (voice) " + expectedQuote + "\n\nIt is green.");
    }

    [Fact]
    public async Task A_Refused_Users_Audio_Is_Never_Downloaded_Or_Transcribed()
    {
        var transcriber = Transcriber();
        var download = Bytes();
        var sink = new RecordingResponseSink(FineGrained);
        var conductor = Conductor(AnsweringRouter(), transcriber, new AccessOptions { AllowedUserIds = ["owner"] });

        await conductor.HandleAsync(Voice(download, userId: "stranger"), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("Scout only answers its configured users.");
        download.Calls.ShouldBe(0);
        await transcriber.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default!, default);
    }

    [Fact]
    public async Task Without_A_Transcriber_The_Reply_Is_The_Exact_Help_Text_And_Nothing_Is_Downloaded()
    {
        var router = AnsweringRouter();
        var download = Bytes();
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(router, transcriber: null).HandleAsync(Voice(download), sink, CancellationToken.None);

        sink.FinalText.ShouldBe(
            "Voice messages need a Whisper model. Download one in the Scout app under AI Sources, Local Models.");
        download.Calls.ShouldBe(0);
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task A_Voice_Message_Declared_Over_25_MB_Is_Refused_Before_Download()
    {
        var download = Bytes();
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(AnsweringRouter(), Transcriber())
            .HandleAsync(Voice(download, sizeBytes: 25L * 1024 * 1024 + 1), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("That voice message is too large. Scout transcribes up to 25 MB.");
        download.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_Download_Larger_Than_25_MB_Is_Refused_Even_When_The_Declared_Size_Was_Small()
    {
        var transcriber = Transcriber();
        var download = Bytes(25 * 1024 * 1024 + 1);
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(AnsweringRouter(), transcriber).HandleAsync(Voice(download), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("That voice message is too large. Scout transcribes up to 25 MB.");
        await transcriber.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_Voice_Message_Longer_Than_5_Minutes_Is_Refused_Before_Download()
    {
        var download = Bytes();
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(AnsweringRouter(), Transcriber())
            .HandleAsync(Voice(download, duration: TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("That voice message is too long. Scout transcribes up to 5 minutes.");
        download.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_Failing_Download_Gets_A_Clear_Reply_And_Never_Reaches_The_Model()
    {
        var router = AnsweringRouter();
        var transcriber = Transcriber();
        var download = new CountingDownload(() => throw new HttpRequestException("503 Service Unavailable"));
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(router, transcriber).HandleAsync(Voice(download), sink, CancellationToken.None);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe("Scout could not download that voice message. Try sending it again.");
        await transcriber.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default!, default);
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task A_Failing_Transcriber_Gets_A_Clear_Reply_And_Never_Reaches_The_Model()
    {
        var router = AnsweringRouter();
        var transcriber = Substitute.For<IAudioTranscriber>();
        transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidDataException("not an ogg stream"));
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(router, transcriber).HandleAsync(Voice(Bytes()), sink, CancellationToken.None);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe("Scout could not transcribe that voice message. Try again or type the question.");
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task A_Voice_Message_While_A_Recording_Holds_The_Model_Is_Told_To_Try_Again_In_A_Few_Minutes()
    {
        var router = AnsweringRouter();
        var transcriber = Substitute.For<IAudioTranscriber>();
        transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new TranscriptionBusyException("held"));
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(router, transcriber).HandleAsync(Voice(Bytes()), sink, CancellationToken.None);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe("Scout is transcribing a recording right now; try the voice message again in a few minutes.");
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task A_Download_Whose_Body_Stalls_Gets_The_Download_Failed_Reply_Within_The_Timeout()
    {
        var router = AnsweringRouter();
        var download = new CountingDownload(() => new StalledStream());
        var sink = new RecordingResponseSink(FineGrained);
        var conductor = new ConversationConductor(router, Pump(), null, null, Transcriber())
        {
            AudioDownloadTimeout = TimeSpan.FromMilliseconds(100),
        };

        await conductor.HandleAsync(Voice(download), sink, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        sink.FinalText.ShouldBe("Scout could not download that voice message. Try sending it again.");
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task The_Transcription_Log_Line_Carries_Its_Duration_In_Ms_And_Never_The_Transcript()
    {
        var logger = new RecordingLogger<ConversationConductor>();

        await Conductor(AnsweringRouter(), Transcriber(), logger: logger)
            .HandleAsync(Voice(Bytes()), new RecordingResponseSink(FineGrained), CancellationToken.None);

        logger.Entries.ShouldContain(e => e.Message.StartsWith("Transcribed a voice message for channel thread-7") && e.Message.Contains("durationMs="));
        logger.Entries.ShouldAllBe(e => !e.Message.Contains("billing service"));
    }

    [Fact]
    public async Task A_Voice_Message_With_No_Speech_Says_So_And_Never_Reaches_The_Model()
    {
        var router = AnsweringRouter();
        var sink = new RecordingResponseSink(FineGrained);

        await Conductor(router, Transcriber("   ")).HandleAsync(Voice(Bytes()), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("Scout heard no speech in that voice message.");
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
