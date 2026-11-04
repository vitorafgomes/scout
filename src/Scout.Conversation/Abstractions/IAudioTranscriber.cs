namespace Scout.Conversation.Abstractions;

/// <summary>
/// Turns recorded speech into text on this machine. Registered only when a
/// speech model is installed; the conductor answers voice messages with setup
/// guidance when none is.
/// </summary>
public interface IAudioTranscriber
{
    /// <summary>
    /// Transcribes <paramref name="audio"/> and returns the text heard, possibly empty; throws
    /// <see cref="TranscriptionBusyException"/> when another transcription holds the model too long.
    /// </summary>
    Task<string> TranscribeAsync(Stream audio, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Transcribes at most <paramref name="maxDuration"/> of a 16 kHz mono WAV recording, yielding each segment heard
    /// with where it starts and ends in the recording, in order, as the model hears it.
    /// </summary>
    IAsyncEnumerable<TranscribedSegment> TranscribeSegmentsAsync(Stream wav, TimeSpan maxDuration, CancellationToken cancellationToken);
}

/// <summary>One stretch of speech: where it starts and ends in the recording and the text heard, possibly empty.</summary>
public sealed record TranscribedSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Thrown when a voice message waited too long for the speech model another transcription holds.</summary>
public sealed class TranscriptionBusyException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public TranscriptionBusyException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public TranscriptionBusyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and its <paramref name="innerException"/>.</summary>
    public TranscriptionBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
