namespace Scout.Llm.Abstractions;

/// <summary>
/// The one translation of a provider failure into Scout's own exceptions, for a caller that reaches the chat service
/// without the gateway: a usage or billing refusal (an HTTP 429 that names one, any HTTP 402, or the Claude API's HTTP 400
/// for a credit balance too low) becomes
/// <see cref="LlmQuotaExceededException"/>, any other HTTP 429 <see cref="LlmRateLimitException"/>, an HTTP 401
/// <see cref="LlmKeyRejectedException"/>, and an HTTP 404 whose error type is <c>not_found_error</c> or whose code is
/// <c>model_not_found</c> <see cref="LlmModelNotFoundException"/> naming the model, so no SDK exception type leaves
/// Scout.Llm. The gateway uses the same rule.
/// </summary>
public static class LlmFailures
{
    /// <summary>
    /// The Scout exception <paramref name="exception"/> stands for, or <paramref name="exception"/> itself when it is not a
    /// provider limit, a rejected key or a missing model; <paramref name="model"/> is the model id the call asked for. Never throws.
    /// </summary>
    public static Exception Translate(Exception exception, string? model)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Providers.ProviderFailures.TranslateProviderFailure(exception, model) ?? exception;
    }
}
