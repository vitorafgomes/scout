namespace Scout.Plugin.Chat;

/// <summary>
/// Configuration for the fallback LLM chat router. Bound from
/// <c>Scout:Plugins:Chat</c>. All fields have safe defaults so the plugin
/// works out of the box.
/// </summary>
public sealed class LlmChatOptions
{
    /// <summary>System prompt prepended to every fallback completion.</summary>
    public string SystemPrompt { get; set; } =
        "You are Scout, a read-only chatops assistant. Answer concisely. " +
        "Use plain text only - no markdown headers or code blocks unless the user explicitly asks for code.";

    /// <summary>Sampling temperature passed to the chat completion service.</summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>Maximum output tokens for the completion.</summary>
    public int MaxTokens { get; set; } = 800;
}
