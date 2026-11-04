using Scout.Conversation.Abstractions;

namespace Scout.Operator.Tests.Fakes;

/// <summary>
/// An <see cref="IAudioTranscriber"/> whose segments come from <see cref="Heard"/>; it records each call, the file the WAV
/// stream reads, whether that file was there when the call started, and the limit the caller gave.
/// </summary>
internal sealed class ScriptedTranscriber : IAudioTranscriber
{
    public Func<CancellationToken, IAsyncEnumerable<TranscribedSegment>> Heard { get; init; } = _ => Segments();

    public int Calls { get; private set; }

    public string? ReadFrom { get; private set; }

    public bool FileThere { get; private set; }

    public TimeSpan? Limit { get; private set; }

    /// <summary>The segments, each as start ms, end ms and text, yielded in order.</summary>
    public static async IAsyncEnumerable<TranscribedSegment> Segments(params (int StartMs, int EndMs, string Text)[] segments)
    {
        foreach (var (start, end, text) in segments)
        {
            await Task.Yield();
            yield return new TranscribedSegment(TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end), text);
        }
    }

    /// <summary>A transcription that throws <paramref name="failure"/> before it hears anything.</summary>
    public static async IAsyncEnumerable<TranscribedSegment> Throwing(Exception failure)
    {
        await Task.FromException(failure);
        yield break;
    }

    public Task<string> TranscribeAsync(Stream audio, string contentType, CancellationToken cancellationToken)
        => throw new NotSupportedException("The operator transcribes recordings by segment only.");

    public IAsyncEnumerable<TranscribedSegment> TranscribeSegmentsAsync(Stream wav, TimeSpan maxDuration, CancellationToken cancellationToken)
    {
        Calls++;
        ReadFrom = (wav as FileStream)?.Name;
        FileThere = ReadFrom is not null && File.Exists(ReadFrom);
        Limit = maxDuration;
        return Heard(cancellationToken);
    }
}
