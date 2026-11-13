#pragma warning disable SKEXP0001
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.TextToAudio;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;

namespace Scout.Operator.Speech;

/// <summary>
/// Reads a sentence of a reply aloud: the connected provider's text-to-audio service in the chosen voice, one of
/// <see cref="MediaCapabilities.SpeechVoices"/>, or with <see cref="MediaCapabilities.SpeechSettings"/> when none is
/// chosen, as MP3; else, or for a sample of a local voice, the chosen bundled voice, or the default one, through
/// <see cref="PiperSpeech"/>, as a WAV rendered in <paramref name="directory"/>, read back and deleted; else no voice. A
/// chosen voice that is not in its list is refused, never passed on. Nothing else is written and no other account is used.
/// </summary>
internal sealed class ReplySpeech(Kernel kernel, MediaCapabilities media, PiperSpeech piper, string directory) : ISpeechSynthesizer
{
    internal const string ProviderFailed = "The AI Source could not read that aloud; see Scout logs.";

    internal const string LocalFailed = "Scout's local voice could not read that aloud; see Scout logs.";

    public async Task<SpeechVoices> VoicesAsync(CancellationToken ct)
    {
        var local = directory.Length == 0 ? [] : await piper.ProbeAsync(ct).ConfigureAwait(false);
        return new SpeechVoices(
            media.Provider,
            Provider() is null ? [] : media.SpeechVoices,
            [.. local.Select(v => new SpeechVoice(v.Id, v.Label))],
            PiperSpeech.Default(local)?.Id);
    }

    public async Task<SpeechAudio?> SynthesizeAsync(string text, SpeechChoice choice, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(choice);
        if (!choice.LocalOnly && Provider() is { } speech)
        {
            return await ProviderAsync(speech, text, SettingsFor(choice.ProviderVoice), ct).ConfigureAwait(false);
        }

        if (directory.Length == 0 || LocalVoice(await piper.ProbeAsync(ct).ConfigureAwait(false), choice.LocalVoice) is not { } voice)
        {
            return null;
        }

        var file = $"speech-{Guid.NewGuid():N}.wav";
        try
        {
            await piper.SynthesizeAsync(voice.Id, text, 1, directory, file, ct).ConfigureAwait(false);
            return new SpeechAudio("audio/wav", await File.ReadAllBytesAsync(MediaFiles.PathFor(directory, file), ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(LocalFailed, ex);
        }
        finally
        {
            try
            {
                File.Delete(MediaFiles.PathFor(directory, file));
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }
    }

    private ITextToAudioService? Provider()
        => media.Speech ? kernel.Services.GetService<ITextToAudioService>() : null;

    private PromptExecutionSettings? SettingsFor(string? voice)
        => voice is null
            ? media.SpeechSettings
            : media.SpeechSettingsFor is { } settingsFor && media.SpeechVoices.Contains(voice, StringComparer.Ordinal)
                ? settingsFor(voice)
                : throw new InvalidOperationException(SpeechVoices.Unknown(voice));

    private static PiperVoice? LocalVoice(IReadOnlyList<PiperVoice> voices, string? chosen)
        => chosen is null || voices.Count == 0
            ? PiperSpeech.Default(voices)
            : voices.FirstOrDefault(v => v.Id == chosen) ?? throw new InvalidOperationException(SpeechVoices.Unknown(chosen));

    private static async Task<SpeechAudio> ProviderAsync(ITextToAudioService speech, string text, PromptExecutionSettings? settings, CancellationToken ct)
    {
        ReadOnlyMemory<byte>? bytes;
        try
        {
            var audio = await speech.GetAudioContentsAsync(text, settings, kernel: null, ct).ConfigureAwait(false);
            bytes = audio.Count > 0 ? audio[0].Data : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(ProviderFailed, ex);
        }

        return bytes is { IsEmpty: false } data
            ? new SpeechAudio("audio/mpeg", data.ToArray())
            : throw new InvalidOperationException(ProviderFailed);
    }
}
