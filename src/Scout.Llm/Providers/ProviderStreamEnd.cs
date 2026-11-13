using Microsoft.SemanticKernel;
using OpenAI.Responses;

namespace Scout.Llm.Providers;

/// <summary>
/// Reads how a provider ended a stream from one update: the finish reason or status it gave, and the failure code of a
/// Responses API stream that ended failed or with an error. Never the provider's text.
/// </summary>
internal static class ProviderStreamEnd
{
#pragma warning disable OPENAI001
    /// <summary>The finish reason or terminal status <paramref name="update"/> carries, or null when it carries none.</summary>
    internal static string? FinishOf(StreamingChatMessageContent update) => update.InnerContent switch
    {
        StreamingResponseIncompleteUpdate incomplete => $"incomplete ({incomplete.Response?.IncompleteStatusDetails?.Reason})",
        StreamingResponseFailedUpdate failed => $"failed ({failed.Response?.Error?.Code})",
        StreamingResponseErrorUpdate error => $"error ({error.Code})",
        StreamingResponseRefusalDoneUpdate => "refusal",
        StreamingResponseCompletedUpdate completed => completed.Response?.Status?.ToString(),
        _ => update.Metadata is { } metadata && metadata.TryGetValue("FinishReason", out var reason) ? reason?.ToString() : null,
    };

    /// <summary>The failure code of a Responses API update that ended the stream failed or with an error, <c>unknown</c> when it names none, or null.</summary>
    internal static string? FailureOf(object? inner) => inner switch
    {
        StreamingResponseFailedUpdate failed => failed.Response?.Error?.Code.ToString() ?? "unknown",
        StreamingResponseErrorUpdate error => string.IsNullOrEmpty(error.Code) ? "unknown" : error.Code,
        _ => null,
    };
#pragma warning restore OPENAI001
}
