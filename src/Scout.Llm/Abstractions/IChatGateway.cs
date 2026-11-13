namespace Scout.Llm.Abstractions;

/// <summary>
/// Primary chat API of the Scout LLM gateway. Streaming-first: consumers default
/// to <see cref="StreamAsync"/> and never touch Semantic Kernel types. No SK
/// types appear in this contract, so consumers stay provider-agnostic.
/// </summary>
public interface IChatGateway
{
    /// <summary>
    /// Stream the assistant response as content chunks arrive. The primary path.
    /// </summary>
    /// <param name="request">The chat request. Must be non-null with a non-empty user message.</param>
    /// <param name="cancellationToken">Cancels the stream mid-flight.</param>
    /// <returns>An async sequence of non-empty content chunks in arrival order.</returns>
    IAsyncEnumerable<string> StreamAsync(ChatRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Convenience for the rare caller that needs the whole text at once. Drains
    /// <see cref="StreamAsync"/> into a single string so there is one code path.
    /// </summary>
    Task<string> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}
