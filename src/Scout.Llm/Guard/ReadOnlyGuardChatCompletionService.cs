using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Scout.Llm.Guard;

/// <summary>
/// Decorates the provider's <see cref="IChatCompletionService"/> in the shared kernel.
/// Before the model runs, TypeSafe's Jev judges whether the latest user message, read
/// with the turns before it, asks Scout to change something outside the conversation;
/// at or above the threshold the
/// inner service is never called and a refusal is returned instead. When TypeSafe
/// fails, the request goes to the inner service unchanged.
/// </summary>
internal sealed class ReadOnlyGuardChatCompletionService(
    IChatCompletionService inner,
    TypeSafeClient typeSafe,
    double threshold,
    ILogger<ReadOnlyGuardChatCompletionService> logger) : IChatCompletionService
{
    internal const string Refusal =
        "Scout is read-only: it can explain, summarise and draft, but it will not change code, " +
        "infrastructure, configuration, data or accounts. If you want a change, Scout can " +
        "describe the steps for you to apply.";

    internal const string Instructions =
        "Judge `latest_user_message` in the context of `recent_turns`: does it ask the assistant to " +
        "make a change outside this conversation, such as creating, editing, deleting, deploying, " +
        "merging, approving, sending, posting elsewhere, restarting, scaling, or changing permissions " +
        "in code, infrastructure, configuration, data, tickets or accounts? A short confirmation like " +
        "'ok, do it' counts as a change request when the turns before it proposed one.";

    internal const string CriteriaTrue = "The user wants the assistant to perform such a change.";

    internal const string CriteriaFalse =
        "The user asks a question, wants an explanation, a summary, a plan, a review, or a draft " +
        "that a person will apply themselves; or the message is small talk.";

    // ponytail: mirrors Scout.Conversation's default memory of 6 exchanges (MaxTurns); Scout.Llm cannot reference it, so a changed default must be copied here.
    private const int RecentTurns = 12;
    private const int MaxTurnLength = 1000;
    private const int MaxLatestLength = 4000;

    private readonly IChatCompletionService _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TypeSafeClient _typeSafe = typeSafe ?? throw new ArgumentNullException(nameof(typeSafe));
    private readonly ILogger<ReadOnlyGuardChatCompletionService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public IReadOnlyDictionary<string, object?> Attributes => _inner.Attributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        if (await RefusesAsync(chatHistory, cancellationToken).ConfigureAwait(false))
        {
            return [new ChatMessageContent(AuthorRole.Assistant, Refusal)];
        }

        return await _inner.GetChatMessageContentsAsync(
            chatHistory, executionSettings, kernel, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (await RefusesAsync(chatHistory, cancellationToken).ConfigureAwait(false))
        {
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, Refusal);
            yield break;
        }

        await foreach (var chunk in _inner.GetStreamingChatMessageContentsAsync(
            chatHistory, executionSettings, kernel, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private async Task<bool> RefusesAsync(ChatHistory chatHistory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatHistory);
        var turns = chatHistory
            .Where(message => message.Role == AuthorRole.User || message.Role == AuthorRole.Assistant)
            .ToList();
        var latestIndex = turns.FindLastIndex(message => message.Role == AuthorRole.User);
        var latest = latestIndex < 0 ? null : turns[latestIndex].Content;
        if (string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        var state = new GuardState(
            turns.Take(latestIndex)
                .Where(message => !string.IsNullOrWhiteSpace(message.Content))
                .TakeLast(RecentTurns)
                .Select(message => new GuardTurn(
                    message.Role == AuthorRole.User ? "user" : "assistant",
                    Tail(message.Content!, MaxTurnLength)))
                .ToList(),
            Tail(latest, MaxLatestLength));

        double probability;
        try
        {
            probability = await _typeSafe.AskNoulAsync(
                state, Instructions, CriteriaTrue, CriteriaFalse, cancellationToken).ConfigureAwait(false);
        }
        catch (TypeSafeException ex)
        {
            _logger.LogWarning(
                "Read-only guard could not reach a verdict (status={Status}): {Reason} The request goes to the model unchecked.",
                ex.StatusCode is { } status ? (int)status : null, ex.Message);
            return false;
        }

        if (probability < threshold)
        {
            return false;
        }

        _logger.LogInformation("Read-only guard refused a request (p={P:0.00})", probability);
        return true;
    }

    private static string Tail(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[^maxLength..];

    private sealed record GuardState(
        [property: JsonPropertyName("recent_turns")] IReadOnlyList<GuardTurn> RecentTurns,
        [property: JsonPropertyName("latest_user_message")] string LatestUserMessage);

    private sealed record GuardTurn(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("text")] string Text);
}
