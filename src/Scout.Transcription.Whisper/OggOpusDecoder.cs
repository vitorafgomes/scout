using Concentus;
using Concentus.Oggfile;

namespace Scout.Transcription.Whisper;

/// <summary>
/// Decodes an Ogg Opus stream (the format of a Discord voice message) into the
/// 16 kHz mono float samples Whisper expects, with a managed decoder so no
/// ffmpeg or native Opus library is needed. The decoder runs at Opus's native
/// 48 kHz, because the managed decoder returns silence for SILK (voice-mode)
/// streams decoded straight to 16 kHz, and every three samples are averaged
/// down to one.
/// </summary>
internal static class OggOpusDecoder
{
    internal const int SampleRate = 16_000;
    private const int DecodeRate = 48_000;
    private const int Ratio = DecodeRate / SampleRate;

    /// <summary>
    /// Decodes at most <paramref name="maxDuration"/> of audio; anything after
    /// that is ignored.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream holds no decodable Opus audio.</exception>
    public static float[] DecodeMono16k(Stream ogg, TimeSpan maxDuration)
    {
        var maxSamples = (int)(maxDuration.TotalSeconds * SampleRate);
        var samples = new List<float>();
        var reader = new OpusOggReadStream(OpusCodecFactory.CreateDecoder(DecodeRate, 1), ogg);
        while (reader.HasNextPacket && samples.Count < maxSamples)
        {
            var packet = reader.DecodeNextPacket();
            if (packet is null)
            {
                continue;
            }
            for (var i = 0; i + Ratio <= packet.Length && samples.Count < maxSamples; i += Ratio)
            {
                samples.Add((packet[i] + packet[i + 1] + packet[i + 2]) / (Ratio * 32768f));
            }
        }

        if (samples.Count == 0)
        {
            throw new InvalidDataException(
                "The voice message holds no Ogg Opus audio" + (reader.LastError is { } error ? $": {error}" : "."));
        }
        return [.. samples];
    }
}
