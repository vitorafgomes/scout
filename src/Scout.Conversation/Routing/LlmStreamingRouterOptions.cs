namespace Scout.Conversation.Routing;

/// <summary>
/// Settings for <see cref="LlmStreamingRouter"/>. Bound from
/// "Scout:Conversation".
/// </summary>
public sealed class LlmStreamingRouterOptions
{
    /// <summary>System prompt prepended to every routed message.</summary>
    public string SystemPrompt { get; set; } =
        "You are Scout, a read-only chatops assistant. Answer concisely.";

    /// <summary>Optional sampling temperature. Forwarded only when set.</summary>
    public double? Temperature { get; set; }

    /// <summary>Optional output token cap. Forwarded only when set.</summary>
    public int? MaxTokens { get; set; }
}
