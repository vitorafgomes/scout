#pragma warning disable SKEXP0001
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.TextToAudio;
using Scout.Llm.Abstractions;

namespace Scout.Operator;

/// <summary>The narration of a scene: its audio source, the file in the media directory, its duration and a note.</summary>
internal sealed record NarrationResult(string AudioSource, string? AudioFile, int DurationMs, string? Note);

/// <summary>
/// Makes a scene's narration in the voice the mandate names: a bundled <c>piper:</c> voice through
/// <see cref="PiperSpeech"/>, saved as <c>scene-&lt;id&gt;.wav</c>, or <c>&lt;Provider&gt;:&lt;voice&gt;</c>, one of the
/// connected provider's <see cref="MediaCapabilities.SpeechVoices"/>, through its text-to-audio service, saved as
/// <c>scene-&lt;id&gt;.mp3</c> in the project's media directory. With no voice named, the provider's speech service in its
/// default voice, else the default bundled voice, else no audio and a note. A voice that is neither fails the scene. The
/// duration is estimated at <see cref="WordsPerMinute"/> words a minute, because no engine reports the audio's length.
/// </summary>
internal sealed class NarrationGenerator(Kernel kernel, MediaCapabilities media, PiperSpeech piper)
{
    internal const int WordsPerMinute = 150;

    internal const string NoSpeech =
        "No narration: the connected AI Source has no speech service in Scout; attach your own audio in Scout Desktop, Production.";

    internal static string Failed(int order) =>
        $"The AI Source could not make the narration of scene {order}; regenerate it in Scout Desktop, Production.";

    internal static int EstimateMs(string narration)
        => narration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length * 60_000 / WordsPerMinute;

    /// <summary>Whether a scene could be narrated now: the connected provider has speech, or a bundled voice is usable.</summary>
    public async Task<bool> SpeechAvailableAsync(CancellationToken ct)
        => media.Speech || (await piper.ProbeAsync(ct).ConfigureAwait(false)).Count > 0;

    public async Task<NarrationResult> GenerateAsync(OperatorScene scene, string? voice, string directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var estimate = EstimateMs(scene.Narration);
        var chosen = voice?.Trim() ?? string.Empty;
        var speech = kernel.Services.GetService<ITextToAudioService>();
        if (chosen.StartsWith(PiperSpeech.Prefix, StringComparison.Ordinal))
        {
            return await BundledAsync(scene, chosen, directory, estimate, ct).ConfigureAwait(false);
        }

        if (chosen.Length > 0)
        {
            var separator = chosen.IndexOf(':', StringComparison.Ordinal);
            var name = separator > 0 ? chosen[(separator + 1)..] : string.Empty;
            if (speech is null
                || media.SpeechSettingsFor is not { } settingsFor
                || separator <= 0
                || chosen[..separator] != media.Provider
                || !media.SpeechVoices.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(PiperSpeech.UnknownVoice(chosen));
            }

            return await ProviderAsync(speech, scene, settingsFor(name), directory, estimate, ct).ConfigureAwait(false);
        }

        if (speech is not null)
        {
            return await ProviderAsync(speech, scene, media.SpeechSettings, directory, estimate, ct).ConfigureAwait(false);
        }

        return PiperSpeech.Default(await piper.ProbeAsync(ct).ConfigureAwait(false)) is { } fallback
            ? await BundledAsync(scene, fallback.Id, directory, estimate, ct).ConfigureAwait(false)
            : new NarrationResult(OperatorPipeline.NoAudio, null, estimate, NoSpeech);
    }

    private async Task<NarrationResult> BundledAsync(OperatorScene scene, string voice, string directory, int estimate, CancellationToken ct)
    {
        var file = $"scene-{scene.Id}.wav";
        await piper.SynthesizeAsync(voice, scene.Narration, scene.Order, directory, file, ct).ConfigureAwait(false);
        return new NarrationResult(OperatorPipeline.Generated, file, estimate, null);
    }

    private static async Task<NarrationResult> ProviderAsync(
        ITextToAudioService speech, OperatorScene scene, PromptExecutionSettings? settings, string directory, int estimate, CancellationToken ct)
    {
        ReadOnlyMemory<byte>? bytes;
        try
        {
            var audio = await speech.GetAudioContentsAsync(scene.Narration, settings, kernel: null, ct).ConfigureAwait(false);
            bytes = audio.Count > 0 ? audio[0].Data : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(Failed(scene.Order), ex);
        }

        if (bytes is not { IsEmpty: false } data)
        {
            throw new InvalidOperationException(Failed(scene.Order));
        }

        var file = $"scene-{scene.Id}.mp3";
        await MediaFiles.WriteAsync(directory, file, data, ct).ConfigureAwait(false);
        return new NarrationResult(OperatorPipeline.Generated, file, estimate, null);
    }
}
