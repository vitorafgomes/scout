namespace Scout.Conversation.Abstractions;

/// <summary>
/// Tells the desktop app that a voice message arrived while no speech model is installed, so it can offer one.
/// The host supplies the implementation; it receives the chat platform only, never the message or its sender.
/// </summary>
public interface IVoiceNoticeSignal
{
    /// <summary>Report that a voice message from <paramref name="platform"/> was not transcribed for lack of a model.</summary>
    void NoSpeechModel(string platform);
}
