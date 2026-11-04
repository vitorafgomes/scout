using Microsoft.SemanticKernel;

namespace Scout.Llm.Abstractions;

/// <summary>
/// Which media services the connected provider's Semantic Kernel connector registered on the shared kernel:
/// text to audio (<see cref="Speech"/>) and text to image (<see cref="Images"/>), with the execution settings each
/// call passes so a caller needs no connector type. The speech settings ask for MP3 in a named voice; the image
/// settings ask for a landscape picture returned as bytes, never as a link to fetch.
/// </summary>
/// <param name="Speech">An <c>ITextToAudioService</c> is on the kernel.</param>
/// <param name="Images">An <c>ITextToImageService</c> is on the kernel.</param>
/// <param name="Provider">The provider's name, as <c>Scout:Llm:Provider</c> selects it.</param>
public sealed record MediaCapabilities(bool Speech, bool Images, string Provider)
{
    /// <summary>The settings a text-to-audio call passes; null without speech.</summary>
    public PromptExecutionSettings? SpeechSettings { get; init; }

    /// <summary>The voice names the speech service offers, in the provider's own words; empty without speech.</summary>
    public IReadOnlyList<string> SpeechVoices { get; init; } = [];

    /// <summary>The settings a text-to-audio call passes for one of <see cref="SpeechVoices"/>; null without speech.</summary>
    public Func<string, PromptExecutionSettings>? SpeechSettingsFor { get; init; }

    /// <summary>The settings a text-to-image call passes; null without images.</summary>
    public PromptExecutionSettings? ImageSettings { get; init; }
}
