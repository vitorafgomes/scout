using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Routing;

/// <summary>
/// Single-responder router for v1. Maps the inbound message text to a
/// <see cref="ChatRequest"/> and returns a streaming <see cref="ChatResponse"/>
/// whose tokens come from <see cref="IChatGateway.StreamAsync"/>. Consumes the
/// gateway, never the raw chat-completion service.
/// </summary>
public sealed class LlmStreamingRouter : IConversationRouter
{
    private readonly IChatGateway _gateway;
    private readonly LlmStreamingRouterOptions _options;

    public LlmStreamingRouter(IChatGateway gateway, IOptions<LlmStreamingRouterOptions> options)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException(
            "LlmStreamingRouterOptions must be configured.", nameof(options));
    }

    /// <inheritdoc />
    public ChatResponse Route(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var request = new ChatRequest
        {
            SystemPrompt = _options.SystemPrompt,
            UserMessage = message.Text,
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
        };

        // StreamAsync is lazy: the gateway is not invoked until the pump
        // enumerates. The cancellation token flows in at enumeration time.
        return new ChatResponse(StreamWithToken(request));
    }

    private async IAsyncEnumerable<string> StreamWithToken(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var token in _gateway.StreamAsync(request, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return token;
        }
    }
}
