using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Whisper.net;

namespace Scout.Transcription.Whisper.Tests;

public sealed class WhisperLiveTranscriberTests : IDisposable
{
    private const int Rate = 16_000;
    private readonly string _fakeModel = Path.Combine(Path.GetTempPath(), $"scout-fake-ggml-{Guid.NewGuid():N}.bin");

    public WhisperLiveTranscriberTests() => File.WriteAllBytes(_fakeModel, new byte[1024]);

    public void Dispose() => File.Delete(_fakeModel);

    private static byte[] Speech(double seconds)
    {
        var samples = (int)(seconds * Rate);
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            BitConverter.GetBytes((short)(16_000 * Math.Sin(2 * Math.PI * 440 * i / Rate))).CopyTo(bytes, i * 2);
        }
        return bytes;
    }

    private static byte[] Silence(double seconds) => new byte[(int)(seconds * Rate) * 2];

    private sealed class Handle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private sealed class FakeModel
    {
        private readonly object _gate = new();
        private readonly List<(int Samples, WhisperAudioTranscriber.Decoding Decoding)> _decodes = [];

        public SemaphoreSlim Entered { get; } = new(0);

        public SemaphoreSlim? Proceed { get; init; }

        public string? PartialLanguage { get; init; }

        public string? FinalLanguage { get; init; }

        public int Loads { get; private set; }

        public int Unloads { get; private set; }

        public IReadOnlyList<(int Samples, WhisperAudioTranscriber.Decoding Decoding)> Decodes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _decodes];
                }
            }
        }

        public WhisperAudioTranscriber Transcriber(TimeProvider? time = null) => new(
            "model.bin",
            _ =>
            {
                Loads++;
                return new WhisperAudioTranscriber.LoadedModel(Decode, new Handle(() => Unloads++));
            },
            time ?? TimeProvider.System);

        private async IAsyncEnumerable<(TranscribedSegment, string?)> Decode(
            float[] samples, WhisperAudioTranscriber.Decoding decoding, [EnumeratorCancellation] CancellationToken ct)
        {
            lock (_gate)
            {
                _decodes.Add((samples.Length, decoding));
            }
            Entered.Release();
            if (Proceed is not null)
            {
                await Proceed.WaitAsync(ct);
            }
            yield return decoding.BeamSize is null
                ? (new TranscribedSegment(TimeSpan.Zero, TimeSpan.Zero, $" partial {samples.Length} "), PartialLanguage)
                : (new TranscribedSegment(TimeSpan.Zero, TimeSpan.Zero, $" final {samples.Length} "), FinalLanguage);
        }
    }

    private sealed class Heard : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<LiveTranscript> _events = [];
        private readonly SemaphoreSlim _arrived = new(0);

        public void Dispose() => _arrived.Dispose();

        public IReadOnlyList<LiveTranscript> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public Task OnEvent(LiveTranscript transcript)
        {
            lock (_gate)
            {
                _events.Add(transcript);
            }
            _arrived.Release();
            return Task.CompletedTask;
        }

        public async Task WaitForAsync(int count)
        {
            while (Events.Count < count)
            {
                (await _arrived.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue(
                    $"expected {count} live events, got {Events.Count}: {string.Join(", ", Events)}");
            }
        }
    }

    [Fact]
    public async Task Each_Window_Decodes_Greedy_And_Emits_A_Partial()
    {
        var model = new FakeModel();
        using var whisper = model.Transcriber();
        using var heard = new Heard();
        var session = new WhisperLiveTranscriber(whisper).Start(heard.OnEvent, CancellationToken.None);

        await session.FeedAsync(Speech(1), CancellationToken.None);
        await heard.WaitForAsync(1);

        heard.Events[0].ShouldBe(new LiveTranscript("partial 12000", false));
        var (samples, decoding) = model.Decodes.ShouldHaveSingleItem();
        samples.ShouldBe(12_000, "the first window is the first 0.75 s of the utterance");
        decoding.Greedy.ShouldBeTrue();
        decoding.BeamSize.ShouldBeNull();
        decoding.NoContext.ShouldBeTrue();
        decoding.SingleSegment.ShouldBeTrue();
        decoding.AudioContextSize.ShouldBe(384, "a 0.75 s window needs the smallest audio context");
    }

    [Theory]
    [InlineData(38_400, 384)]
    [InlineData(128_000, 500)]
    [InlineData(320_000, 1000)]
    public void A_Partial_Audio_Context_Covers_The_Window_And_2_s_More_From_384_To_1000(int samples, int audioContext)
        => WhisperAudioTranscriber.Decoding.LivePartialOver(samples, null).AudioContextSize.ShouldBe(
            audioContext, $"a {samples / 16_000.0} s window, at 20 ms per audio context frame");

    [Fact]
    public async Task An_Ended_Utterance_Is_Redecoded_With_Beam_5()
    {
        var model = new FakeModel();
        using var whisper = model.Transcriber();
        using var heard = new Heard();
        var session = new WhisperLiveTranscriber(whisper).Start(heard.OnEvent, CancellationToken.None);

        await session.FeedAsync(Speech(0.5), CancellationToken.None);
        await session.FeedAsync(Silence(1), CancellationToken.None);
        await heard.WaitForAsync(1);

        heard.Events.ShouldBe([new LiveTranscript("final 8000", true)]);
        var (samples, decoding) = model.Decodes.ShouldHaveSingleItem();
        samples.ShouldBe(8_000, "the final decode covers the whole utterance");
        decoding.BeamSize.ShouldBe(5);
        decoding.Greedy.ShouldBeFalse();
        decoding.NoContext.ShouldBeFalse();
        decoding.AudioContextSize.ShouldBeNull();
    }

    [Fact]
    public async Task A_Window_During_A_Decode_Is_Dropped()
    {
        var model = new FakeModel { Proceed = new SemaphoreSlim(0) };
        using var whisper = model.Transcriber();
        using var heard = new Heard();
        var session = new WhisperLiveTranscriber(whisper).Start(heard.OnEvent, CancellationToken.None);

        await session.FeedAsync(Speech(1), CancellationToken.None);
        (await model.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the first window reaches the model");
        await session.FeedAsync(Speech(1.2), CancellationToken.None);
        model.Proceed.Release(10);
        await session.StopAsync(CancellationToken.None);

        model.Decodes.Where(d => d.Decoding.Greedy).Select(d => d.Samples).ShouldBe(
            [12_000], "the windows at 1.35 s and 1.95 s arrived while the first decoded");
        heard.Events.ShouldBe([new LiveTranscript("partial 12000", false), new LiveTranscript("final 35200", true)]);
    }

    [Fact]
    public async Task Partials_After_The_First_Final_Use_Its_Language()
    {
        var model = new FakeModel { PartialLanguage = "en", FinalLanguage = "pt" };
        using var whisper = model.Transcriber();
        using var heard = new Heard();
        var session = new WhisperLiveTranscriber(whisper).Start(heard.OnEvent, CancellationToken.None);

        await session.FeedAsync(Speech(1), CancellationToken.None);
        await heard.WaitForAsync(1);
        await session.FeedAsync(Silence(1), CancellationToken.None);
        await heard.WaitForAsync(2);
        for (var chunk = 0; chunk < 100 && heard.Events.Count < 3; chunk++)
        {
            await session.FeedAsync(Speech(0.1), CancellationToken.None);
            await Task.Delay(20);
        }
        await session.StopAsync(CancellationToken.None);

        model.Decodes.Where(d => d.Decoding.Greedy).Select(d => d.Decoding.Language).ShouldBe(
            [null, "pt"], "the first utterance's partials detect; later partials use the first final's language");
        model.Decodes.Where(d => !d.Decoding.Greedy).ShouldAllBe(d => d.Decoding.Language == null, "finals still detect");
    }

    [Fact]
    public async Task A_Dropped_Window_Copies_No_Samples()
    {
        var model = new FakeModel { Proceed = new SemaphoreSlim(0) };
        using var whisper = model.Transcriber();
        var session = new WhisperLiveTranscriber(whisper).Start(_ => Task.CompletedTask, CancellationToken.None);
        await session.FeedAsync(Speech(1), CancellationToken.None);
        (await model.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the first window reaches the model");
        var tenSeconds = Speech(10);

        var before = GC.GetAllocatedBytesForCurrentThread();
        await session.FeedAsync(tenSeconds, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        model.Proceed.Release(10);
        await session.StopAsync(CancellationToken.None);

        model.Decodes.Count(d => d.Decoding.Greedy).ShouldBe(1, "the 17 windows in those 10 s arrived while the first decoded");
        allocated.ShouldBeLessThan(
            12_000 * sizeof(float), "the dropped windows together allocated less than one 0.75 s copy of samples");
    }

    [Fact]
    public async Task A_Window_Is_Dropped_While_A_Final_Is_Still_Decoding()
    {
        var model = new FakeModel { Proceed = new SemaphoreSlim(0) };
        using var whisper = model.Transcriber();
        using var heard = new Heard();
        var session = new WhisperLiveTranscriber(whisper).Start(heard.OnEvent, CancellationToken.None);

        var chunk = Speech(0.5).Concat(Silence(1)).Concat(Speech(1)).ToArray();
        await session.FeedAsync(chunk, CancellationToken.None);
        (await model.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the final reaches the model");
        model.Proceed.Release(10);
        await session.StopAsync(CancellationToken.None);

        model.Decodes.Where(d => d.Decoding.Greedy).ShouldBeEmpty("the second utterance's window arrived while the first final was queued");
        model.Decodes.Select(d => d.Samples).ShouldBe([8000, 16000]);
    }

    [Fact]
    public async Task The_Final_Is_Never_Dropped()
    {
        var model = new FakeModel { Proceed = new SemaphoreSlim(0) };
        using var whisper = model.Transcriber();
        using var heard = new Heard();
        var session = new WhisperLiveTranscriber(whisper).Start(heard.OnEvent, CancellationToken.None);

        await session.FeedAsync(Speech(1), CancellationToken.None);
        (await model.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the first window reaches the model");
        await session.FeedAsync(Silence(1), CancellationToken.None);
        await session.FeedAsync(Speech(0.5), CancellationToken.None);
        await session.FeedAsync(Silence(1), CancellationToken.None);
        model.Proceed.Release(10);
        var stopping = session.StopAsync(CancellationToken.None);
        await heard.WaitForAsync(3);
        await stopping;

        heard.Events.ShouldBe([
            new LiveTranscript("partial 12000", false),
            new LiveTranscript("final 16000", true),
            new LiveTranscript("final 8000", true),
        ]);
    }

    [Fact]
    public async Task Stop_Flushes_The_Open_Utterance_As_One_Final()
    {
        var model = new FakeModel();
        using var whisper = model.Transcriber();
        using var heard = new Heard();
        var session = new WhisperLiveTranscriber(whisper).Start(heard.OnEvent, CancellationToken.None);

        await session.FeedAsync(Speech(0.5), CancellationToken.None);
        await session.StopAsync(CancellationToken.None);

        heard.Events.ShouldBe([new LiveTranscript("final 8000", true)]);
    }

    [Fact]
    public async Task Voice_Messages_And_Live_Sessions_Share_One_Loaded_Model()
    {
        var model = new FakeModel();
        using var whisper = model.Transcriber();
        var session = new WhisperLiveTranscriber(whisper).Start(_ => Task.CompletedTask, CancellationToken.None);
        await session.FeedAsync(Speech(0.5), CancellationToken.None);
        await session.StopAsync(CancellationToken.None);

        await using var ogg = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-440hz-1500ms.ogg"));
        await whisper.TranscribeAsync(ogg, "audio/ogg", CancellationToken.None);

        model.Loads.ShouldBe(1);
        model.Decodes.Select(d => d.Decoding).ShouldBe([
            WhisperAudioTranscriber.Decoding.LiveFinal,
            WhisperAudioTranscriber.Decoding.VoiceMessage,
        ]);
    }

    [Fact]
    public async Task The_Model_Unloads_10_Idle_Minutes_After_A_Live_Session()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
        var model = new FakeModel();
        using var whisper = model.Transcriber(clock);
        var session = new WhisperLiveTranscriber(whisper).Start(_ => Task.CompletedTask, CancellationToken.None);
        await session.FeedAsync(Speech(0.5), CancellationToken.None);
        await session.StopAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(9));
        model.Unloads.ShouldBe(0, "the model stays loaded before 10 idle minutes");
        clock.Advance(TimeSpan.FromMinutes(1));

        model.Unloads.ShouldBe(1);
    }

    [Fact]
    public async Task A_File_That_Is_Not_A_Whisper_Model_Fails_The_Live_Session_With_A_Model_Load_Error()
    {
        using var whisper = new WhisperAudioTranscriber(_fakeModel);
        using var heard = new Heard();
        var session = new WhisperLiveTranscriber(whisper).Start(heard.OnEvent, CancellationToken.None);

        await session.FeedAsync(Speech(0.5), CancellationToken.None);

        await Should.ThrowAsync<WhisperModelLoadException>(() => session.StopAsync(CancellationToken.None));
        heard.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Decode_Failure_Surfaces_On_The_Next_Feed()
    {
        using var whisper = new WhisperAudioTranscriber(_fakeModel);
        var session = new WhisperLiveTranscriber(whisper).Start(_ => Task.CompletedTask, CancellationToken.None);

        await session.FeedAsync(Speech(1), CancellationToken.None);
        Exception? thrown = null;
        for (var attempt = 0; attempt < 500 && thrown is null; attempt++)
        {
            try
            {
                await session.FeedAsync(Silence(0.02), CancellationToken.None);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            await Task.Delay(20);
        }

        thrown.ShouldBeOfType<WhisperModelLoadException>(
            "the first window failed to decode, so a later feed rethrows that failure");
    }
}
