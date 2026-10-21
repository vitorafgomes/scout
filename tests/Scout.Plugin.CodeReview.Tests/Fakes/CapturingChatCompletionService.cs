using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Scout.Plugin.CodeReview.Tests.Fakes;

/// <summary>
/// Capturing fake <see cref="IChatCompletionService"/>. Records the last history,
/// settings, and kernel it was called with, and returns a configurable reply so
/// reviewer behavior can be asserted without a real model.
/// </summary>
internal sealed class CapturingChatCompletionService : IChatCompletionService
{
    private readonly string _reply;

    public CapturingChatCompletionService(string reply = "ok")
    {
        _reply = reply;
    }

    public ChatHistory? LastHistory { get; private set; }

    public PromptExecutionSettings? LastSettings { get; private set; }

    public Kernel? LastKernel { get; private set; }

    public bool KernelWasNull { get; private set; }

    public IReadOnlyDictionary<string, object?> Attributes { get; } =
        new Dictionary<string, object?>();

    public string? SystemMessage =>
        LastHistory?.FirstOrDefault(m => m.Role == AuthorRole.System)?.Content;

    public string? UserMessage =>
        LastHistory?.FirstOrDefault(m => m.Role == AuthorRole.User)?.Content;

    public OpenAIPromptExecutionSettings? OpenAiSettings =>
        LastSettings as OpenAIPromptExecutionSettings;

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        LastHistory = chatHistory;
        LastSettings = executionSettings;
        LastKernel = kernel;
        KernelWasNull = kernel is null;

        IReadOnlyList<ChatMessageContent> result =
            new[] { new ChatMessageContent(AuthorRole.Assistant, _reply) };
        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastHistory = chatHistory;
        LastSettings = executionSettings;
        LastKernel = kernel;
        KernelWasNull = kernel is null;

        yield return new StreamingChatMessageContent(AuthorRole.Assistant, _reply);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// Fake <see cref="IChatCompletionService"/> that returns an empty reply so the
/// reviewer's empty-response handling can be asserted.
/// </summary>
internal sealed class EmptyChatCompletionService : IChatCompletionService
{
    public IReadOnlyDictionary<string, object?> Attributes { get; } =
        new Dictionary<string, object?>();

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessageContent> result =
            new[] { new ChatMessageContent(AuthorRole.Assistant, "   ") };
        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new StreamingChatMessageContent(AuthorRole.Assistant, "   ");
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
