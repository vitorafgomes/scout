namespace Scout.Llm.Abstractions;

/// <summary>
/// Signals that the model ended its answer with no text, and why, in a sentence written for the owner: cut by the
/// output limit, the context window full, a refusal, a paused or a silent turn. <see cref="System.Exception.Message"/>
/// is that sentence and never the provider's text.
/// </summary>
public sealed class LlmNoAnswerException(string sentence) : Exception(sentence)
{
}
