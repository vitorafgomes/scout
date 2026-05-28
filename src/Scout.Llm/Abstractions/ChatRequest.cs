namespace Scout.Llm.Abstractions;

/// <summary>
/// Minimal, provider-agnostic chat request. Carries a single user turn plus an
/// optional system prompt and generation settings. History lists, tools, and
/// function-calling are intentionally out of scope here — that belongs to the
/// future agent-router project.
/// </summary>
public sealed record ChatRequest
{
    /// <summary>
    /// Optional system prompt. When null or whitespace, no system message is
    /// sent.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The user message. Required; must be non-empty.</summary>
    public required string UserMessage { get; init; }

    /// <summary>Optional sampling temperature. Forwarded only when set.</summary>
    public double? Temperature { get; init; }

    /// <summary>Optional output token cap. Forwarded only when set.</summary>
    public int? MaxTokens { get; init; }
}
