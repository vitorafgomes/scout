#pragma warning disable SKEXP0001
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.TextToAudio;
using Scout.Llm.Abstractions;

namespace Scout.Operator;

/// <summary>The narration of a scene: its audio source, the file in the media directory, its duration and a note.</summary>
internal sealed record NarrationResult(string AudioSource, string? AudioFile, int DurationMs, string? Note);

/// <summary>
/// Makes a scene's narration with the connected provider's text-to-audio service, saved as <c>scene-&lt;id&gt;.mp3</c>
/// in the project's media directory. Without that service the scene has no audio and a note. The duration is
/// estimated at <see cref="WordsPerMinute"/> words a minute, because neither connector reports the audio's length.
/// </summary>
internal sealed class NarrationGenerator(Kernel kernel, MediaCapabilities media)
{
    internal const int WordsPerMinute = 150;

    internal const string NoSpeech =
        "No narration: the connected AI Source has no speech service in Scout; attach your own audio in Scout Desktop, Production.";

    internal static string Failed(int order) =>
        $"The AI Source could not make the narration of scene {order}; regenerate it in Scout Desktop, Production.";

    internal static int EstimateMs(string narration)
        => narration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length * 60_000 / WordsPerMinute;

    public async Task<NarrationResult> GenerateAsync(OperatorScene scene, string directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var estimate = EstimateMs(scene.Narration);
        var speech = kernel.Services.GetService<ITextToAudioService>();
        if (speech is null)
        {
            return new NarrationResult(OperatorPipeline.NoAudio, null, estimate, NoSpeech);
        }

        ReadOnlyMemory<byte>? bytes;
        try
        {
            var audio = await speech.GetAudioContentsAsync(scene.Narration, media.SpeechSettings, kernel: null, ct)
                .ConfigureAwait(false);
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
