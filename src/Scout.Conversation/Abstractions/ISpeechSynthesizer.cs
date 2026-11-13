namespace Scout.Conversation.Abstractions;

/// <summary>A sentence read aloud: the audio's content type, <c>audio/mpeg</c> or <c>audio/wav</c>, and its bytes.</summary>
public sealed record SpeechAudio(string ContentType, byte[] Bytes);

/// <summary>
/// The voice the owner chose for a sentence: <paramref name="ProviderVoice"/>, one of the AI Source's voices, used when
/// the AI Source has speech; <paramref name="LocalVoice"/>, one of Scout's local voices, used otherwise; either null for
/// the default. <paramref name="LocalOnly"/> reads with the local voice even when the AI Source has speech, for a sample.
/// </summary>
public sealed record SpeechChoice(string? ProviderVoice = null, string? LocalVoice = null, bool LocalOnly = false)
{
    /// <summary>No voice chosen: the AI Source's default voice, else the default local voice.</summary>
    public static readonly SpeechChoice Default = new();
}

/// <summary>One of Scout's local voices: the id a <see cref="SpeechChoice"/> names and the label the owner reads.</summary>
public sealed record SpeechVoice(string Id, string Label);

/// <summary>
/// The voices this machine can read with: the AI Source's name and its voices, empty when it has no speech; Scout's local
/// voices, empty when there are none; and the local voice used when the owner chose none.
/// </summary>
public sealed record SpeechVoices(string Provider, IReadOnlyList<string> ProviderVoices, IReadOnlyList<SpeechVoice> LocalVoices, string? DefaultLocal)
{
    /// <summary>The reason a voice that is not in these lists is refused.</summary>
    public static string Unknown(string voice) =>
        $"The voice '{voice}' is not available on this machine; choose another in Scout Desktop, Models, Speech.";
}

/// <summary>
/// Reads a sentence of a reply aloud for the local owner, with the connected AI Source's speech when it has one, else
/// Scout's local voice. No other account is used.
/// </summary>
public interface ISpeechSynthesizer
{
    /// <summary>
    /// The audio of <paramref name="text"/> in the voice <paramref name="choice"/> names, or null when this machine has no
    /// voice; a voice that fails, or one that is not in <see cref="VoicesAsync"/>, throws an
    /// <see cref="InvalidOperationException"/> whose message the owner can read.
    /// </summary>
    Task<SpeechAudio?> SynthesizeAsync(string text, SpeechChoice choice, CancellationToken ct);

    /// <summary>The voices this machine can read with now.</summary>
    Task<SpeechVoices> VoicesAsync(CancellationToken ct);
}
