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
/// system prompt, or, when the selector cannot select, lists the skill catalog
/// there so the model loads the matching skill itself. A skill that declares a
/// function outside <see cref="ChatFunctions.General"/>, a write, runs only when the
/// message's own text selects it, never its context alone (a transcript, screen text).
/// A turn that selects no skill runs the skills of the conversation's previous turn
/// while that turn is younger than the memory's idle expiry, and a message carrying
/// one of the desktop's <see cref="ChatFunctions.MarkerSkills"/> also runs that skill.
/// The request carries <see cref="ChatFunctions.For"/> those skills, so the model is
/// offered the general set and only the functions the turn's skills declare. A turn
/// whose skills declare functions sends no output limit, so the provider's own limit
/// applies; any other turn sends <see cref="LlmStreamingRouterOptions.MaxTokens"/>. When
/// conversation memory is enabled it replays prior turns into the request and
/// writes the completed user/assistant pair back on clean stream completion.
/// </summary>
public sealed class LlmStreamingRouter : IConversationRouter, ICarriedSkills
{
    private readonly IChatGateway _gateway;
    private readonly ISkillSelector _skillSelector;
    private readonly IConversationMemoryStore _memoryStore;
    private readonly IConversationKeyProvider _keyProvider;
    private readonly ConversationMemoryOptions _memoryOptions;
    private readonly LlmStreamingRouterOptions _options;
    private readonly ISkillCatalog? _skillCatalog;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, (IReadOnlyList<Skill> Skills, DateTimeOffset Touched)> _carried = new(StringComparer.Ordinal);
    private readonly Queue<string> _carriedOrder = new();
    private readonly Lock _carryGate = new();

    /// <summary>How many conversations keep the skills of their previous turn; the oldest is forgotten first.</summary>
    internal const int CarryCapacity = 256;

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmStreamingRouter"/> class.
    /// </summary>
    /// <param name="gateway">The chat gateway.</param>
    /// <param name="skillSelector">The skill selector.</param>
    /// <param name="memoryStore">The per-conversation history store.</param>
    /// <param name="keyProvider">The conversation key provider.</param>
    /// <param name="memoryOptions">The conversation memory options.</param>
    /// <param name="options">The router options.</param>
    /// <param name="skillCatalog">The loaded skills, where a marker's skill is found; none when skills are off.</param>
    /// <param name="timeProvider">The clock the carried skills expire by; the system clock when null.</param>
    public LlmStreamingRouter(
        IChatGateway gateway,
        ISkillSelector skillSelector,
        IConversationMemoryStore memoryStore,
        IConversationKeyProvider keyProvider,
        IOptions<ConversationMemoryOptions> memoryOptions,
        IOptions<LlmStreamingRouterOptions> options,
        ISkillCatalog? skillCatalog = null,
        TimeProvider? timeProvider = null)
    {
        _skillCatalog = skillCatalog;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        var userMessage = message.Context is { Length: > 0 } context ? message.Text + context : message.Text;
        var key = _keyProvider.DeriveKey(message);
        var selected = await _skillSelector.SelectAsync(userMessage, cancellationToken)
            .ConfigureAwait(false);
        if (userMessage.Length > message.Text.Length && selected.Any(DeclaresWrite))
        {
            var own = await _skillSelector.SelectAsync(message.Text, cancellationToken).ConfigureAwait(false);
            selected = [.. selected.Where(skill => !DeclaresWrite(skill) || own.Any(o => string.Equals(o.Name, skill.Name, StringComparison.OrdinalIgnoreCase)))];
        }

        var skills = TurnSkills(key, message.Text, selected);
        var systemPrompt = SkillPromptComposer.Compose(
            _options.SystemPrompt, skills, _skillSelector.ModelChoiceCatalog);

        var request = new ChatRequest
        {
            SystemPrompt = systemPrompt,
            UserMessage = userMessage,
            Temperature = _options.Temperature,
            MaxTokens = skills.Any(skill => skill.Functions.Count > 0) ? null : _options.MaxTokens,
            Image = message.Image is { } image ? new ChatImage(image.ContentType, image.Bytes) : null,
            Functions = ChatFunctions.For(skills),
            Skills = [.. skills.Select(skill => skill.Name)],
        };

        if (_memoryOptions.Enabled)
        {
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
        if (_memoryOptions.Enabled && assembled.Length > 0)
        {
            _memoryStore.Append(
                key,
                new ConversationTurn(ConversationRole.User, message.Text),
                new ConversationTurn(ConversationRole.Assistant, assembled.ToString()),
                cancellationToken);
        }
    }

    private List<Skill> TurnSkills(string key, string text, IReadOnlyList<Skill> selected)
    {
        var skills = ReviewStaysReview(text, selected, Carried(key));
        foreach (var (marker, name) in ChatFunctions.MarkerSkills)
        {
            if (text.Contains(marker, StringComparison.Ordinal)
                && !skills.Any(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase))
                && _skillCatalog?.Find(name) is { } skill)
            {
                skills.Add(skill);
            }
        }

        if (skills.Count > 0)
        {
            Carry(key, skills);
        }

        return skills;
    }

    /// <inheritdoc />
    public void Forget(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_carryGate)
        {
            _carried.Remove(key);
        }
    }

    private static bool DeclaresWrite(Skill skill) =>
        skill.Functions.Any(function => !ChatFunctions.General.Contains(function, StringComparer.Ordinal));

    private List<Skill> Carried(string key)
    {
        lock (_carryGate)
        {
            return _carried.TryGetValue(key, out var carried)
                && _timeProvider.GetUtcNow() - carried.Touched < _memoryOptions.IdleTtl
                ? [.. carried.Skills]
                : [];
        }
    }

    private void Carry(string key, IReadOnlyList<Skill> skills)
    {
        lock (_carryGate)
        {
            if (!_carried.ContainsKey(key))
            {
                _carriedOrder.Enqueue(key);
                if (_carriedOrder.Count > CarryCapacity)
                {
                    _carried.Remove(_carriedOrder.Dequeue());
                }
            }

            _carried[key] = ([.. skills], _timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// The selected skills, or the carried ones when none is selected; in a review, a turn whose skills or carried skills
    /// hold code-review, code-audit is dropped unless the message's own text names the audit or asks for the whole
    /// repository, and a turn left with no skill keeps the carried ones, so a review is offered the audit's functions
    /// only when the owner asks for the audit.
    /// </summary>
    private static List<Skill> ReviewStaysReview(string text, IReadOnlyList<Skill> selected, List<Skill> carried)
    {
        var skills = selected.Count > 0 ? [.. selected] : carried;
        if (!skills.Concat(carried).Any(skill => Named(skill, "code-review")) || SkillSelector.AsksForAnAudit(text))
        {
            return skills;
        }

        skills.RemoveAll(skill => Named(skill, "code-audit"));
        return skills.Count > 0 ? skills : carried;
    }

    private static bool Named(Skill skill, string name) => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase);
}
