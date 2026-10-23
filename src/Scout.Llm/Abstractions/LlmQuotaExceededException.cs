namespace Scout.Llm.Abstractions;

/// <summary>
/// Signals that the provider refused the request because the account has no
/// credit left or reached a spending or usage limit (an HTTP 429 that a retry
/// never fixes). <see cref="Code"/> carries the provider's error code; neither it
/// nor <see cref="System.Exception.Message"/> ever carries a credential.
/// </summary>
public sealed class LlmQuotaExceededException : Exception
{
    public LlmQuotaExceededException(string code, Exception? inner = null)
        : base($"LLM provider quota or billing limit reached (HTTP 429, code {code}).", inner)
    {
        Code = code;
    }

    /// <summary>The provider's error code, for example <c>credit_balance_exhausted</c>.</summary>
    public string Code { get; }
}
