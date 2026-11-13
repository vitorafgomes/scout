using System.ClientModel;
using System.Net;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers;

/// <summary>
/// Reads a provider's HTTP failure, whichever connector raised it, so the gateway, the live translator and
/// <see cref="LlmFailures"/> know only Scout's own exceptions.
/// </summary>
internal static class ProviderFailures
{
    private static readonly HashSet<string> QuotaErrorCodes = new(StringComparer.Ordinal)
    {
        "credit_balance_exhausted",
        "organization_spend_limit_exceeded",
        "project_spend_limit_exceeded",
        "organization_usage_limit_exceeded",
        "subscription_sharing_usage_limit_exceeded",
    };

    private static readonly HashSet<string> AccountRefusalCodes = new(StringComparer.Ordinal)
    {
        "credit_balance_too_low",
        "enforced_spend_limit_reached",
        "billing_frozen",
        "organization_on_hold",
        "billing_error",
    };

    /// <summary>
    /// The Scout exception a provider HTTP failure stands for, so no SDK exception type leaves this project: a 429 is a
    /// quota or billing refusal (<see cref="LlmQuotaExceededException"/>) or a rate limit (<see cref="LlmRateLimitException"/>),
    /// a 402 is a billing refusal, and a 401 is a rejected key (<see cref="LlmKeyRejectedException"/>, without the provider's
    /// text). A 404 whose content's error type is <c>not_found_error</c> or whose code is <c>model_not_found</c> is a model
    /// the provider does not have (<see cref="LlmModelNotFoundException"/> naming <paramref name="model"/>, never the
    /// provider's text). Any other status whose content's error code or type names an account refusal (a credit balance too
    /// low, a spend limit, a frozen billing, an organisation on hold, a billing error) is a quota refusal with that code. Null
    /// for anything else, so the original exception propagates unchanged. Never throws: it runs inside an exception filter.
    /// </summary>
    internal static Exception? TranslateProviderFailure(Exception ex, string? model)
    {
        var status = ex switch
        {
            ClientResultException client => client.Status,
            HttpOperationException http => (int?)http.StatusCode,
            _ => null,
        };

        return status switch
        {
            (int)HttpStatusCode.TooManyRequests => QuotaErrorCode(ex) is { } code
                ? new LlmQuotaExceededException(code, ex)
                : new LlmRateLimitException("LLM rate limit (HTTP 429).", ex),
            (int)HttpStatusCode.PaymentRequired => new LlmQuotaExceededException(QuotaErrorCode(ex) ?? "payment_required", ex),
            (int)HttpStatusCode.NotFound when IsMissingModel(ex) => new LlmModelNotFoundException(model, ex),
            _ when AccountRefusalCode(ex) is { } code => new LlmQuotaExceededException(code, ex),
            (int)HttpStatusCode.Unauthorized => new LlmKeyRejectedException(),
            _ => null,
        };
    }

    private static string? QuotaErrorCode(Exception ex)
    {
        var (code, type) = ErrorFields(ex);
        if (code is not null && QuotaErrorCodes.Contains(code))
        {
            return code;
        }

        return type == "insufficient_quota" ? code ?? "insufficient_quota" : null;
    }

    private static bool IsMissingModel(Exception ex)
    {
        var (code, type) = ErrorFields(ex);
        return type == "not_found_error" || code == "model_not_found";
    }

    private static string? AccountRefusalCode(Exception ex)
    {
        var (code, type) = ErrorFields(ex);
        return code is not null && AccountRefusalCodes.Contains(code) ? code
            : type is not null && AccountRefusalCodes.Contains(type) ? type
            : null;
    }

    private static (string? Code, string? Type) ErrorFields(Exception ex)
    {
        try
        {
            var body = ex switch
            {
                ClientResultException client => client.GetRawResponse()?.Content?.ToString(),
                HttpOperationException http => http.ResponseContent,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(body))
            {
                return (null, null);
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (StringProperty(error, "code"), StringProperty(error, "type"));
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static string? StringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
