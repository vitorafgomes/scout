namespace Scout.Llm.Abstractions;

/// <summary>
/// Receives what each answer used, as the provider reported it, and the moment a plan's usage limit is hit. The host
/// supplies the implementation; a call never leaves the worker except as a line for the desktop.
/// </summary>
public interface ILlmUsageSink
{
    /// <summary>One completed answer, with its usage summed over every model call it made.</summary>
    /// <param name="usage">The usage the provider reported; its token counts are null when it reported none.</param>
    void Answered(LlmUsage usage);

    /// <summary>The provider refused an answer because the plan's usage limit was reached.</summary>
    /// <param name="source">The configured provider name.</param>
    void UsageLimitReached(string source);
}

/// <summary>
/// The usage of one answer: the configured provider name, the model the provider named, the prompt, completion and total
/// tokens, and the cost in US dollars where the provider reports one. A null count means the provider did not report it.
/// </summary>
public sealed record LlmUsage(
    string Source,
    string? Model,
    long? PromptTokens,
    long? CompletionTokens,
    long? TotalTokens,
    decimal? Cost);
