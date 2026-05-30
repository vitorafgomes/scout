using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Skills;

namespace Scout.Conversation.Routing;

/// <summary>
/// Single-responder router for v1. Maps the inbound message text to a
/// <see cref="ChatRequest"/> and returns a streaming <see cref="ChatResponse"/>
/// whose tokens come from <see cref="IChatGateway.StreamAsync"/>. Consumes the
/// gateway, never the raw chat-completion service. Per turn it selects the most
/// relevant skill via <see cref="ISkillSelector"/> and injects it into the
/// system prompt.
/// </summary>
public sealed class LlmStreamingRouter : IConversationRouter
{
    private readonly IChatGateway _gateway;
    private readonly ISkillSelector _skillSelector;
    private readonly LlmStreamingRouterOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmStreamingRouter"/> class.
    /// </summary>
    /// <param name="gateway">The chat gateway.</param>
    /// <param name="skillSelector">The skill selector.</param>
    /// <param name="options">The router options.</param>
    public LlmStreamingRouter(
        IChatGateway gateway,
        ISkillSelector skillSelector,
        IOptions<LlmStreamingRouterOptions> options)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _skillSelector = skillSelector ?? throw new ArgumentNullException(nameof(skillSelector));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException(
            "LlmStreamingRouterOptions must be configured.", nameof(options));
    }

    /// <inheritdoc />
    public ChatResponse Route(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Defer all work (skill selection, request build, streaming) to
        // enumeration so the gateway is not invoked until the pump enumerates.
        return new ChatResponse(StreamWithToken(message.Text));
    }

    private async IAsyncEnumerable<string> StreamWithToken(
        string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var skills = await _skillSelector.SelectAsync(userMessage, cancellationToken)
            .ConfigureAwait(false);
        var systemPrompt = SkillPromptComposer.Compose(_options.SystemPrompt, skills);

        var request = new ChatRequest
        {
            SystemPrompt = systemPrompt,
            UserMessage = userMessage,
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
        };

        await foreach (var token in _gateway.StreamAsync(request, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return token;
        }
    }
}
