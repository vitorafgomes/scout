using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Scout.Llm.Providers.Anthropic;

/// <summary>
/// Keeps the Anthropic client's exceptions inside this folder: an HTTP failure becomes Semantic Kernel's
/// <see cref="HttpOperationException"/> with its status, its error type and the API's own <c>error.message</c>, with the
/// API key redacted, on one line and cut to 500 characters, never the raw response body, which the gateway maps like
/// every other provider's. The response content is Scout's own <c>{"error":{"code":...,"type":...}}</c>: the code is
/// <c>credit_balance_too_low</c> when the message names the credit balance, otherwise the API's
/// <c>error.details.error_code</c>, and the type is the API's <c>error.type</c>; the gateway reads them to tell an
/// account refusal from any other failure.
/// </summary>
internal sealed class AnthropicChatClient(IChatClient inner, string apiKey) : DelegatingChatClient(inner)
{
    private const int SentenceLimit = 500;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (AnthropicApiException ex)
        {
            throw Translated(ex);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var e = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await e.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (AnthropicApiException ex)
            {
                throw Translated(ex);
            }

            yield return WithStopReason(e.Current);
        }
    }

    /// <summary>
    /// Copies the Claude API's own <c>stop_reason</c> of a <c>message_delta</c> event onto the update as its
    /// <c>FinishReason</c> property, which Semantic Kernel carries into the content's metadata. The bridge to Semantic
    /// Kernel drops <see cref="ChatResponseUpdate.FinishReason"/>, and that value folds <c>max_tokens</c> and
    /// <c>model_context_window_exceeded</c> into one; the API's string keeps them apart.
    /// </summary>
    private static ChatResponseUpdate WithStopReason(ChatResponseUpdate update)
    {
        if (update.RawRepresentation is RawMessageStreamEvent raw
            && raw.TryPickDelta(out var delta)
            && delta.Delta.StopReason?.Raw() is { Length: > 0 } reason)
        {
            update.AdditionalProperties ??= [];
            update.AdditionalProperties["FinishReason"] = reason;
        }

        return update;
    }

    private HttpOperationException Translated(AnthropicApiException ex)
    {
        var type = ex.Type?.Raw();
        var status = $"The Anthropic API answered HTTP {(int)ex.StatusCode} ({type ?? "unknown error"})";
        var credit = ex.ApiMessage?.Contains("credit balance", StringComparison.OrdinalIgnoreCase) == true;
        var content = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = credit ? "credit_balance_too_low" : ex.ErrorCode, ["type"] = type },
        }.ToJsonString();
        if (string.IsNullOrWhiteSpace(ex.ApiMessage))
        {
            return new(ex.StatusCode, content, status + ".", innerException: null);
        }

        var sentence = string.Join(' ', ex.ApiMessage.Replace(apiKey, "[redacted]", StringComparison.Ordinal)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return new(
            ex.StatusCode,
            content,
            $"{status}: {(sentence.Length > SentenceLimit ? sentence[..SentenceLimit] : sentence)}",
            innerException: null);
    }
}
