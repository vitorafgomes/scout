using System.Buffers;
using System.Text;

namespace Scout.Transcription.Whisper;

/// <summary>
/// Reads a RIFF WAVE stream recorded at 16 kHz mono, as 16-bit PCM or 32-bit
/// float, into the float samples Whisper expects, without resampling.
/// </summary>
internal static class WavDecoder
{
    private const int SampleRate = 16_000;
    private const ushort PcmFormat = 1;
    private const ushort FloatFormat = 3;
    private const ushort ExtensibleFormat = 0xFFFE;
    private const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// Decodes at most <paramref name="maxDuration"/> of audio; anything after
    /// that is ignored.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is not 16 kHz mono 16-bit PCM or float WAV, or holds no samples.</exception>
    public static float[] DecodeMono16k(Stream wav, TimeSpan maxDuration)
    {
        var maxSamples = (int)(maxDuration.TotalSeconds * SampleRate);
        using var reader = new BinaryReader(wav, Encoding.ASCII, leaveOpen: true);
        try
        {
            if (Tag(reader) != "RIFF")
            {
                throw new InvalidDataException("The recording is not a WAV file.");
            }
            reader.ReadUInt32();
            if (Tag(reader) != "WAVE")
            {
                throw new InvalidDataException("The recording is not a WAV file.");
            }

            ushort? format = null;
            ushort bitsPerSample = 0;
            while (true)
            {
                var id = Tag(reader);
                var size = reader.ReadUInt32();
                if (id == "fmt ")
                {
                    var body = reader.ReadBytes(checked((int)(size + (size & 1))));
                    format = ReadFormat(body, out bitsPerSample);
                }
                else if (id == "data")
                {
                    if (format is null)
                    {
                        throw new InvalidDataException("The WAV file has no format chunk before its data.");
                    }
                    return ReadSamples(reader, size, format.Value, bitsPerSample, maxSamples);
                }
                else
                {
                    reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The WAV file ends before its audio.", ex);
        }
    }

    private static ushort ReadFormat(byte[] body, out ushort bitsPerSample)
    {
        if (body.Length < 16)
        {
            throw new InvalidDataException("The WAV format chunk is too short.");
        }
        var format = BitConverter.ToUInt16(body, 0);
        var channels = BitConverter.ToUInt16(body, 2);
        var rate = BitConverter.ToUInt32(body, 4);
        bitsPerSample = BitConverter.ToUInt16(body, 14);
        if (format == ExtensibleFormat && body.Length >= 26)
        {
            format = BitConverter.ToUInt16(body, 24);
        }

        var supported = (format == PcmFormat && bitsPerSample == 16) || (format == FloatFormat && bitsPerSample == 32);
        if (!supported || channels != 1 || rate != SampleRate)
        {
            throw new InvalidDataException(
                $"Only 16 kHz mono 16-bit PCM or float WAV is supported, not format {format}, {channels} channel(s), {rate} Hz, {bitsPerSample} bits.");
        }
        return format;
    }

    private static float[] ReadSamples(BinaryReader reader, uint size, ushort format, ushort bitsPerSample, int maxSamples)
    {
        var bytesPerSample = bitsPerSample / 8;
        var count = (int)Math.Min(size / (uint)bytesPerSample, (uint)maxSamples);
        var samples = new float[count];
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var read = 0;
        try
        {
            while (read < count)
            {
                var want = Math.Min(count - read, ChunkBytes / bytesPerSample) * bytesPerSample;
                var got = reader.BaseStream.ReadAtLeast(buffer.AsSpan(0, want), want, throwOnEndOfStream: false) / bytesPerSample;
                for (var i = 0; i < got; i++)
                {
                    samples[read + i] = format == PcmFormat
                        ? BitConverter.ToInt16(buffer, i * 2) / 32768f
                        : BitConverter.ToSingle(buffer, i * 4);
                }

                read += got;
                if (got * bytesPerSample < want)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (read == 0)
        {
            throw new InvalidDataException("The WAV file holds no audio.");
        }

        if (read < count)
        {
            Array.Resize(ref samples, read);
        }

        return samples;
    }

    private static string Tag(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length < 4)
        {
            throw new EndOfStreamException();
        }
        return Encoding.ASCII.GetString(bytes);
    }
}
