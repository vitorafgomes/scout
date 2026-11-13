using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Scout.Llm.Providers.None;

/// <summary>
/// Chat completion that answers every request with
/// <see cref="NoneLlmProviderFactory.NoProviderMessage"/> and calls no model.
/// </summary>
internal sealed class NoneChatCompletionService : IChatCompletionService
{
    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ChatMessageContent> reply =
            [new ChatMessageContent(AuthorRole.Assistant, NoneLlmProviderFactory.NoProviderMessage)];
        return Task.FromResult(reply);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return new StreamingChatMessageContent(
            AuthorRole.Assistant, NoneLlmProviderFactory.NoProviderMessage);
    }
}
