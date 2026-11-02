namespace Scout.Conversation.Abstractions;

/// <summary>
/// Turns recorded speech into text on this machine. Registered only when a
/// speech model is installed; the conductor answers voice messages with setup
/// guidance when none is.
/// </summary>
public interface IAudioTranscriber
{
    /// <summary>Transcribes <paramref name="audio"/> and returns the text heard, possibly empty.</summary>
    Task<string> TranscribeAsync(Stream audio, string contentType, CancellationToken cancellationToken);
}
