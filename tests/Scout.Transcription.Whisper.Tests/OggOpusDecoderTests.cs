namespace Scout.Transcription.Whisper.Tests;

public sealed class OggOpusDecoderTests
{
    private static FileStream Fixture() => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-440hz-1500ms.ogg"));

    [Fact]
    public void A_Voice_Message_Decodes_To_16_kHz_Mono_Samples_Of_Its_Length()
    {
        using var ogg = Fixture();

        var samples = OggOpusDecoder.DecodeMono16k(ogg, TimeSpan.FromMinutes(5));

        samples.Length.ShouldBeInRange(23_000, 25_000, "1.5 s at 16 kHz is 24,000 samples plus codec padding");
        samples.Max(MathF.Abs).ShouldBeGreaterThan(0.1f, "the tone must survive decoding, not come out silent");
    }

    [Fact]
    public void Decoding_Stops_At_The_Maximum_Duration()
    {
        using var ogg = Fixture();

        var samples = OggOpusDecoder.DecodeMono16k(ogg, TimeSpan.FromSeconds(0.5));

        samples.Length.ShouldBe(8_000);
    }

    [Fact]
    public void Bytes_That_Are_Not_Ogg_Opus_Are_Refused_As_Invalid_Data()
    {
        using var garbage = new MemoryStream("this is not an ogg stream at all"u8.ToArray());

        Should.Throw<InvalidDataException>(() => OggOpusDecoder.DecodeMono16k(garbage, TimeSpan.FromMinutes(5)));
    }
}
