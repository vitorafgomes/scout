using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers;

/// <summary>
/// The execution settings every provider is called with, built as the OpenAI connector's settings type because each
/// connector reads that type its own way: the OpenAI-family connectors (Mistral and Hugging Face included) and Azure AI
/// Inference send the request's temperature and output limit, and Gemini sends neither of them.
/// </summary>
internal static class ProviderSettings
{
    /// <summary>
    /// The settings of a chat turn: automatic function calling only when the per-turn kernel holds a function, so the
    /// declared set is offered and tool round trips are handled internally, and no function choice at all otherwise; the
    /// temperature and the output limit only when the request sets them.
    /// </summary>
    internal static OpenAIPromptExecutionSettings BuildSettings(ChatRequest request, bool offerFunctions)
    {
        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = offerFunctions ? FunctionChoiceBehavior.Auto() : null,
        };

        if (request.Temperature is { } temperature)
        {
            settings.Temperature = temperature;
        }
        if (request.MaxTokens is { } maxTokens)
        {
            settings.MaxTokens = maxTokens;
        }

        return settings;
    }

    /// <summary>The settings of a live translation: a 400-token cap and no function choice.</summary>
    internal static OpenAIPromptExecutionSettings LiveTranslation() => new() { MaxTokens = 400 };
}
