using System.Runtime.CompilerServices;
using System.Text;
using Scout.Conversation.Abstractions;

namespace Scout.Transcription.Whisper.Tests;

public sealed class WhisperWavTranscriptionTests
{
    private sealed class NoHandle : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class CapturingModel
    {
        public float[]? Samples { get; private set; }
        public int Loads { get; private set; }

        public IReadOnlyList<TranscribedSegment> Heard { get; init; } = [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), " heard ")];

        public WhisperAudioTranscriber Transcriber() => new(
            "model.bin",
            _ =>
            {
                Loads++;
                return new WhisperAudioTranscriber.LoadedModel(Segments, new NoHandle());
            },
            TimeProvider.System);

        private async IAsyncEnumerable<(TranscribedSegment, string?)> Segments(
            float[] samples, WhisperAudioTranscriber.Decoding decoding, [EnumeratorCancellation] CancellationToken ct)
        {
            Samples = samples;
            foreach (var segment in Heard)
            {
                await Task.Yield();
                yield return (segment, null);
            }
        }
    }

    private static MemoryStream Wav(
        int sampleCount,
        Func<int, short>? sample = null,
        int rate = 16_000,
        short channels = 1,
        bool withListChunk = false)
    {
        var data = new byte[sampleCount * 2 * channels];
        for (var i = 0; i < sampleCount * channels; i++)
        {
            BitConverter.GetBytes(sample?.Invoke(i) ?? 0).CopyTo(data, i * 2);
        }
        return Riff(Fmt(1, channels, rate, 16), data, withListChunk);
    }

    private static MemoryStream FloatWav(params float[] samples)
    {
        var data = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            BitConverter.GetBytes(samples[i]).CopyTo(data, i * 4);
        }
        return Riff(Fmt(3, 1, 16_000, 32), data, withListChunk: false);
    }

    private static byte[] Fmt(short format, short channels, int rate, short bits)
    {
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write(format);
        w.Write(channels);
        w.Write(rate);
        w.Write(rate * channels * bits / 8);
        w.Write((short)(channels * bits / 8));
        w.Write(bits);
        return body.ToArray();
    }

    private static MemoryStream Riff(byte[] fmt, byte[] data, bool withListChunk)
    {
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(fmt.Length);
        w.Write(fmt);
        if (withListChunk)
        {
            w.Write(Encoding.ASCII.GetBytes("LIST"));
            w.Write(3);
            w.Write(new byte[] { 1, 2, 3, 0 });
        }
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(data.Length);
        w.Write(data);

        var file = new MemoryStream();
        var f = new BinaryWriter(file);
        f.Write(Encoding.ASCII.GetBytes("RIFF"));
        f.Write((int)body.Length);
        f.Write(body.ToArray());
        file.Position = 0;
        return file;
    }

    [Fact]
    public async Task A_16_kHz_Mono_16_Bit_Wav_Recording_Reaches_The_Model_As_Float_Samples()
    {
        var model = new CapturingModel();
        using var transcriber = model.Transcriber();
        await using var wav = Wav(3, i => new short[] { 16384, -32768, 0 }[i]);

        var text = await transcriber.TranscribeAsync(wav, "audio/wav", CancellationToken.None);

        text.ShouldBe("heard");
        model.Samples.ShouldBe([0.5f, -1f, 0f]);
    }

    [Fact]
    public async Task A_Float_Wav_Recording_Reaches_The_Model_Unchanged()
    {
        var model = new CapturingModel();
        using var transcriber = model.Transcriber();
        await using var wav = FloatWav(0.25f, -0.75f);

        await transcriber.TranscribeAsync(wav, "audio/wav", CancellationToken.None);

        model.Samples.ShouldBe([0.25f, -0.75f]);
    }

    [Fact]
    public async Task Chunks_Between_The_Format_And_The_Audio_Are_Skipped()
    {
        var model = new CapturingModel();
        using var transcriber = model.Transcriber();
        await using var wav = Wav(2, _ => 16384, withListChunk: true);

        await transcriber.TranscribeAsync(wav, "audio/wav", CancellationToken.None);

        model.Samples.ShouldBe([0.5f, 0.5f]);
    }

    [Fact]
    public async Task A_Wav_Recording_Longer_Than_5_Minutes_Reaches_The_Model_Cut_At_5_Minutes_Of_Samples()
    {
        var model = new CapturingModel();
        using var transcriber = model.Transcriber();
        await using var wav = Wav(6 * 60 * 16_000);

        await transcriber.TranscribeAsync(wav, "audio/wav", CancellationToken.None);

        model.Samples!.Length.ShouldBe(5 * 60 * 16_000);
    }

    [Fact]
    public async Task A_60_Second_Wav_Recording_Reaches_The_Model_Sample_For_Sample_And_A_Cut_Short_One_Up_To_Its_Last_Whole_Sample()
    {
        const int Samples = 60 * 16_000;
        static short At(int i) => unchecked((short)(i * 7919));
        var model = new CapturingModel();
        using var transcriber = model.Transcriber();
        await using var wav = Wav(Samples, At);
        await using var cutShort = Wav(Samples, At);
        cutShort.SetLength(cutShort.Length - 3);

        await foreach (var _ in transcriber.TranscribeSegmentsAsync(wav, TimeSpan.FromMinutes(60), CancellationToken.None))
        {
        }
        var whole = model.Samples!;
        await foreach (var _ in transcriber.TranscribeSegmentsAsync(cutShort, TimeSpan.FromMinutes(60), CancellationToken.None))
        {
        }
        var cut = model.Samples!;

        whole.Length.ShouldBe(Samples);
        Enumerable.Range(0, Samples).Count(i => whole[i] != At(i) / 32768f).ShouldBe(0);
        cut.Length.ShouldBe(Samples - 2);
        cut.ShouldBe(whole[..(Samples - 2)]);
    }

    [Theory]
    [InlineData(44_100, 1)]
    [InlineData(16_000, 2)]
    public async Task A_Wav_Recording_That_Is_Not_16_kHz_Mono_Is_Invalid_Data_Before_The_Model_Loads(int rate, short channels)
    {
        var model = new CapturingModel();
        using var transcriber = model.Transcriber();
        await using var wav = Wav(4, rate: rate, channels: channels);

        await Should.ThrowAsync<InvalidDataException>(
            () => transcriber.TranscribeAsync(wav, "audio/wav", CancellationToken.None));

        model.Loads.ShouldBe(0);
    }

    [Fact]
    public async Task Bytes_That_Are_Not_A_Wav_File_Are_Invalid_Data()
    {
        var model = new CapturingModel();
        using var transcriber = model.Transcriber();
        await using var bytes = new MemoryStream(Encoding.ASCII.GetBytes("not a recording at all"));

        await Should.ThrowAsync<InvalidDataException>(
            () => transcriber.TranscribeAsync(bytes, "audio/wav", CancellationToken.None));

        model.Loads.ShouldBe(0);
    }

    [Fact]
    public async Task TranscribeSegmentsAsync_Yields_Each_Segment_With_Its_Start_And_End()
    {
        TranscribedSegment[] heard =
        [
            new(TimeSpan.Zero, TimeSpan.FromMilliseconds(1200), " Feed the starter daily."),
            new(TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(3500), " Bake it hot."),
        ];
        var model = new CapturingModel { Heard = heard };
        using var transcriber = model.Transcriber();
        await using var wav = Wav(3, _ => 16384);

        var segments = await transcriber.TranscribeSegmentsAsync(wav, TimeSpan.FromMinutes(60), CancellationToken.None).ToListAsync();

        segments.ShouldBe(heard);
        model.Samples.ShouldBe([0.5f, 0.5f, 0.5f]);
    }

    [Fact]
    public async Task TranscribeSegmentsAsync_Reads_Up_To_The_Callers_Limit_Not_Five_Minutes()
    {
        var longer = new CapturingModel();
        var shorter = new CapturingModel();
        using var longerTranscriber = longer.Transcriber();
        using var shorterTranscriber = shorter.Transcriber();
        await using var sixMinutes = Wav(6 * 60 * 16_000);
        await using var sixMinutesAgain = Wav(6 * 60 * 16_000);

        await longerTranscriber.TranscribeSegmentsAsync(sixMinutes, TimeSpan.FromMinutes(60), CancellationToken.None).ToListAsync();
        await shorterTranscriber.TranscribeSegmentsAsync(sixMinutesAgain, TimeSpan.FromMinutes(2), CancellationToken.None).ToListAsync();

        longer.Samples!.Length.ShouldBe(6 * 60 * 16_000, "a 60-minute limit takes the whole six minutes");
        shorter.Samples!.Length.ShouldBe(2 * 60 * 16_000, "a 2-minute limit cuts the recording at 2 minutes");
    }

    [Fact]
    public async Task TranscribeSegmentsAsync_Of_A_File_That_Is_Not_Wav_Fails_Before_The_Model_Loads()
    {
        var model = new CapturingModel();
        using var transcriber = model.Transcriber();
        await using var bytes = new MemoryStream(Encoding.ASCII.GetBytes("an mp4 file, not a WAV recording"));

        await Should.ThrowAsync<InvalidDataException>(
            async () => await transcriber.TranscribeSegmentsAsync(bytes, TimeSpan.FromMinutes(60), CancellationToken.None).ToListAsync());

        model.Loads.ShouldBe(0);
        model.Samples.ShouldBeNull();
    }
}
