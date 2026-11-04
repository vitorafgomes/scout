using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Scout.Llm.Providers.OpenAI;

/// <summary>
/// The execution settings of the OpenAI and Azure OpenAI media services: MP3 narration in the voice
/// <c>alloy</c> or one of the other <see cref="Voices"/>, the ones the Semantic Kernel connector maps, and a landscape picture returned as bytes. A <c>dall-e</c> model is asked for 1792x1024 as
/// <c>b64_json</c>; any other model, the <c>gpt-image</c> family, for 1536x1024, which it always returns as bytes, at
/// the quality given, or the service's own default without one.
/// </summary>
internal static class OpenAIMediaSettings
{
    internal const string Voice = "alloy";

    internal static readonly IReadOnlyList<string> Voices = ["alloy", "echo", "fable", "onyx", "nova", "shimmer"];

    internal static PromptExecutionSettings Speech() => Speech(Voice);

#pragma warning disable SKEXP0001
    internal static PromptExecutionSettings Speech(string voice) =>
        new OpenAITextToAudioExecutionSettings(voice) { ResponseFormat = "mp3" };
#pragma warning restore SKEXP0001

    internal static PromptExecutionSettings Image(string model, string? quality = null) =>
        model.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase)
            ? new OpenAITextToImageExecutionSettings { Size = (1792, 1024), ResponseFormat = "b64_json" }
            : new OpenAITextToImageExecutionSettings { Size = (1536, 1024), Quality = quality };
}
