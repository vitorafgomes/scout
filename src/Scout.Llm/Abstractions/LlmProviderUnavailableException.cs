namespace Scout.Llm.Abstractions;

/// <summary>
/// Signals that the LLM provider is unreachable at the transport layer
/// (connection refused, DNS/socket failure, or an internal timeout). The caller
/// should finalize the stream gracefully rather than log it as an error. The
/// <see cref="System.Exception.Message"/> carries a terse internal reason for
/// logging only and is never shown to the user.
/// </summary>
public sealed class LlmProviderUnavailableException : Exception
{
    public LlmProviderUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
