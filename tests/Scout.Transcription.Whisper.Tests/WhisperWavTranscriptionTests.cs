using System.Text;

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

        public WhisperAudioTranscriber Transcriber() => new(
            "model.bin",
            _ =>
            {
                Loads++;
                return new WhisperAudioTranscriber.LoadedModel(
                    (samples, _) =>
                    {
                        Samples = samples;
                        return Task.FromResult(" heard ");
                    },
                    new NoHandle());
            },
            TimeProvider.System);
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
}
