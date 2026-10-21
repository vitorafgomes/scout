using System.Text;
using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;
using Scout.Llm.Abstractions;
using Scout.Skills;

namespace Scout.Conversation.Routing;

/// <summary>
/// Single-responder router for v1. Maps the inbound message to a
/// <see cref="ChatRequest"/> and returns a streaming <see cref="ChatResponse"/>
/// whose tokens come from <see cref="IChatGateway.StreamAsync"/>. Consumes the
/// gateway, never the raw chat-completion service. Per turn it selects the most
/// relevant skill via <see cref="ISkillSelector"/> and injects it into the
/// system prompt. When conversation memory is enabled it replays prior turns
/// into the request and writes the completed user/assistant pair back on clean
/// stream completion.
/// </summary>
public sealed class LlmStreamingRouter : IConversationRouter
{
    private readonly IChatGateway _gateway;
    private readonly ISkillSelector _skillSelector;
    private readonly IConversationMemoryStore _memoryStore;
    private readonly IConversationKeyProvider _keyProvider;
    private readonly ConversationMemoryOptions _memoryOptions;
    private readonly LlmStreamingRouterOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmStreamingRouter"/> class.
    /// </summary>
    /// <param name="gateway">The chat gateway.</param>
    /// <param name="skillSelector">The skill selector.</param>
    /// <param name="memoryStore">The per-conversation history store.</param>
    /// <param name="keyProvider">The conversation key provider.</param>
    /// <param name="memoryOptions">The conversation memory options.</param>
    /// <param name="options">The router options.</param>
    public LlmStreamingRouter(
        IChatGateway gateway,
        ISkillSelector skillSelector,
        IConversationMemoryStore memoryStore,
        IConversationKeyProvider keyProvider,
        IOptions<ConversationMemoryOptions> memoryOptions,
        IOptions<LlmStreamingRouterOptions> options)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _skillSelector = skillSelector ?? throw new ArgumentNullException(nameof(skillSelector));
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        ArgumentNullException.ThrowIfNull(memoryOptions);
        _memoryOptions = memoryOptions.Value ?? throw new ArgumentException(
            "ConversationMemoryOptions must be configured.", nameof(memoryOptions));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException(
            "LlmStreamingRouterOptions must be configured.", nameof(options));
    }

    /// <inheritdoc />
    public ChatResponse Route(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Defer all work (skill selection, history load, request build, streaming)
        // to enumeration so the gateway is not invoked until the pump enumerates.
        return new ChatResponse(StreamWithToken(message));
    }

    private async IAsyncEnumerable<string> StreamWithToken(
        ChatMessage message,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var userMessage = message.Text;
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

        string? key = null;
        if (_memoryOptions.Enabled)
        {
            key = _keyProvider.DeriveKey(message);
            request = request with { History = _memoryStore.Load(key) };
        }

        var assembled = new StringBuilder();
        await foreach (var token in _gateway.StreamAsync(request, cancellationToken)
            .ConfigureAwait(false))
        {
            assembled.Append(token);
            yield return token;
        }

        // Reached only when the stream completes without exception or cancellation,
        // so a failed/partial turn never poisons history.
        if (key is not null && assembled.Length > 0)
        {
            _memoryStore.Append(
                key,
                new ConversationTurn(ConversationRole.User, userMessage),
                new ConversationTurn(ConversationRole.Assistant, assembled.ToString()));
        }
    }
}
