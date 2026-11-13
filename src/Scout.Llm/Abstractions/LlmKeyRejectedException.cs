namespace Scout.Llm.Abstractions;

/// <summary>
/// Signals that a key-based provider rejected its key (HTTP 401): the key is wrong, revoked or expired, and a retry
/// never fixes it. Neither <see cref="System.Exception.Message"/> nor the inner exception's message is the provider's
/// text, which can echo part of the key.
/// </summary>
public sealed class LlmKeyRejectedException() : Exception("The AI provider rejected its key (HTTP 401).")
{
}
