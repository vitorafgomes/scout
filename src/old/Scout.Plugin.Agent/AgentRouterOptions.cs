namespace Scout.Plugin.Agent;

/// <summary>
/// Configuration for the agentic tool-calling router. Bound from
/// <c>Scout:Plugins:Agent</c>. All fields have safe defaults so the
/// router works out of the box. The system prompt is deliberately
/// tool-oriented: it tells the model it owns callable functions and must
/// invoke them to answer, rather than the read-only fallback prompt which
/// suppresses tool use.
/// </summary>
public sealed class AgentRouterOptions
{
    /// <summary>
    /// System prompt prepended to every agentic completion. Instructs the
    /// model to use its registered functions to answer infrastructure and
    /// repository questions. Read-only/descriptive-write remains a safety
    /// boundary, not a denial of capability: the model is encouraged to
    /// call tools, and the exposed surface itself enforces what is allowed.
    /// </summary>
    public string SystemPrompt { get; set; } =
        "You are Scout, a chatops assistant with access to callable tools " +
        "for querying GitHub, Kubernetes, and other infrastructure. " +
        "When a question can be answered by a tool, you MUST call the " +
        "relevant function instead of guessing or replying from memory. " +
        "For requests like \"list my repositories\", call the matching tool " +
        "with an empty owner to mean the authenticated user. " +
        "You may use read tools freely and descriptive-write tools such as " +
        "creating an issue when explicitly asked; never invent destructive " +
        "actions. After the tools return, answer concisely in plain text " +
        "(no markdown headers or code blocks unless the user asks for code).";

    /// <summary>Sampling temperature passed to the chat completion service.</summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>Maximum output tokens for the completion.</summary>
    public int MaxTokens { get; set; } = 800;
}
