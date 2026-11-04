using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Scout.Operator.Tests.Fakes;

/// <summary>
/// A chat model that answers each request with the next scripted reply, or throws a scripted exception, and
/// records each request's messages, its settings and whether a kernel was offered, as they were when sent.
/// </summary>
internal sealed class ScriptedChat(params Func<string>[] replies) : IChatCompletionService
{
    private int _next;

    public List<IReadOnlyList<(AuthorRole Role, string Text)>> Requests { get; } = [];

    public List<(PromptExecutionSettings? Settings, Kernel? Kernel)> Offers { get; } = [];

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public static ScriptedChat Answering(params string[] replies)
        => new(replies.Select(r => (Func<string>)(() => r)).ToArray());

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(chatHistory.Select(m => (m.Role, m.Content ?? string.Empty)).ToList());
        Offers.Add((executionSettings, kernel));
        var reply = replies[Math.Min(_next++, replies.Length - 1)]();
        return Task.FromResult<IReadOnlyList<ChatMessageContent>>([new ChatMessageContent(AuthorRole.Assistant, reply)]);
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The operator never streams.");
}
