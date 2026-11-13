namespace Scout.Llm.Abstractions;

/// <summary>
/// Minimal, provider-agnostic chat request. Carries a single user turn plus an
/// optional system prompt, replayed history, generation settings and the kernel
/// functions the turn may call.
/// </summary>
public sealed record ChatRequest
{
    /// <summary>
    /// Optional system prompt. When null or whitespace, no system message is
    /// sent.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Optional per-turn persona body (WHO answers + tool posture). On the agent path
    /// it is bound to the agent template via KernelArguments["persona"]; on the direct
    /// path it leads the single system message. When null/whitespace, the agent's default
    /// persona (or SystemPrompt alone) applies.
    /// </summary>
    public string? Persona { get; init; }

    /// <summary>
    /// Prior conversation turns to replay, oldest-first, after the system message
    /// and before the current <see cref="UserMessage"/>. Empty (the default) means a
    /// single-turn request with no replayed history.
    /// </summary>
    public IReadOnlyList<ConversationTurn> History { get; init; } = Array.Empty<ConversationTurn>();

    /// <summary>The user message. Required; must be non-empty.</summary>
    public required string UserMessage { get; init; }

    /// <summary>
    /// Optional picture sent with <see cref="UserMessage"/> as an image part of the same user message.
    /// A provider without <see cref="MediaCapabilities.Vision"/> refuses the request instead of dropping it.
    /// </summary>
    public ChatImage? Image { get; init; }

    /// <summary>Optional sampling temperature. Forwarded only when set.</summary>
    public double? Temperature { get; init; }

    /// <summary>Optional output token cap. Forwarded only when set.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// The kernel functions the turn is offered, as <c>Plugin.function</c> names. Null or empty offers the model no
    /// function; a name the kernel does not hold is skipped.
    /// </summary>
    public IReadOnlyList<string>? Functions { get; init; }

    /// <summary>The names of the skills whose functions the turn carries, for the log; empty for a turn with no skill.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];
}
