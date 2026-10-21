namespace Scout.Llm.Abstractions;

/// <summary>
/// Signals a transient, expected provider rate limit (HTTP 429). The caller
/// should finalize the stream gracefully rather than log it as an error. The
/// <see cref="System.Exception.Message"/> carries a terse internal reason for
/// logging only and is never shown to the user.
/// </summary>
public sealed class LlmRateLimitException : Exception
{
    public LlmRateLimitException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
