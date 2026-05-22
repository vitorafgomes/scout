using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Composing;

/// <summary>
/// Turns a question plus retrieved chunks into a grounded
/// <see cref="CodeAnswer"/>. Implementations own the LLM call, the prompt
/// strategy, and the citation mapping.
/// </summary>
public interface IComposer
{
    /// <summary>
    /// Composes an answer from <paramref name="question"/> and the supplied
    /// grounding <paramref name="context"/>.
    /// </summary>
    /// <param name="question">Non-null, non-whitespace user question.</param>
    /// <param name="context">Non-null list of retrieved chunks (may be empty).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<CodeAnswer> ComposeAsync(
        string question,
        IReadOnlyList<VectorSearchResult> context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streaming variant of <see cref="ComposeAsync"/>. Yields raw token
    /// chunks from the underlying chat service as they arrive. Citations
    /// are not part of the stream — callers compute them from the same
    /// <paramref name="context"/> the composer consumes.
    /// </summary>
    /// <param name="question">Non-null, non-whitespace user question.</param>
    /// <param name="context">Non-null list of retrieved chunks (may be empty).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    IAsyncEnumerable<string> ComposeStreamingAsync(
        string question,
        IReadOnlyList<VectorSearchResult> context,
        CancellationToken cancellationToken);
}
