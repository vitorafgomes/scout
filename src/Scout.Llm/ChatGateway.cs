using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;

namespace Scout.Llm;

/// <summary>
/// Streaming-first <see cref="IChatGateway"/> over the shared
/// <see cref="IChatCompletionService"/>. Translates the provider-agnostic
/// <see cref="ChatRequest"/> into Semantic Kernel primitives and yields content
/// chunks as they arrive.
/// </summary>
internal sealed class ChatGateway : IChatGateway
{
    private readonly IChatCompletionService _chat;

    public ChatGateway(IChatCompletionService chat)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.UserMessage))
        {
            throw new ArgumentException(
                "ChatRequest.UserMessage must be non-empty.", nameof(request));
        }

        var history = new ChatHistory();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            history.AddSystemMessage(request.SystemPrompt);
        }
        history.AddUserMessage(request.UserMessage);

        var settings = BuildSettings(request);

        var stream = _chat.GetStreamingChatMessageContentsAsync(
            history, settings, kernel: null, cancellationToken);

        await foreach (var chunk in stream.ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                yield return chunk.Content;
            }
        }
    }

    public async Task<string> CompleteAsync(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        await foreach (var chunk in StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            builder.Append(chunk);
        }
        return builder.ToString();
    }

    private static PromptExecutionSettings? BuildSettings(ChatRequest request)
    {
        if (request.Temperature is null && request.MaxTokens is null)
        {
            return null;
        }

        var extensionData = new Dictionary<string, object>();
        if (request.Temperature is { } temperature)
        {
            extensionData["temperature"] = temperature;
        }
        if (request.MaxTokens is { } maxTokens)
        {
            extensionData["max_tokens"] = maxTokens;
        }

        return new PromptExecutionSettings { ExtensionData = extensionData };
    }
}
