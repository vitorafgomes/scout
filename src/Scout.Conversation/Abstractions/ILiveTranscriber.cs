namespace Scout.Conversation.Abstractions;

/// <summary>
/// Transcribes speech on this machine while it is being spoken. Registered only
/// when a speech model is installed.
/// </summary>
public interface ILiveTranscriber
{
    /// <summary>
    /// Opens a live session; <paramref name="onEvent"/> receives partial text while
    /// an utterance is spoken and one final text when it ends.
    /// </summary>
    ILiveSession Start(Func<LiveTranscript, Task> onEvent, CancellationToken cancellationToken);
}

/// <summary>One live transcription session, fed with audio as it is captured.</summary>
public interface ILiveSession
{
    /// <summary>Feeds 16-bit little-endian mono PCM at 16 kHz; a chunk may end mid-sample.</summary>
    ValueTask FeedAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken);

    /// <summary>Ends the session, emitting the final text of any utterance still open.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Text heard so far in an utterance, or its final text when <paramref name="Final"/> is true.</summary>
public sealed record LiveTranscript(string Text, bool Final);
