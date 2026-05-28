namespace Scout.Plugin.CodeQA.Embedding;

/// <summary>
/// Converts blocks of text into dense float vectors for similarity search.
/// Implementations are the only boundary the CodeQA plugin has with an
/// embedding model provider; swapping providers means swapping the
/// registration, never the callers.
/// </summary>
public interface IEmbedder
{
    /// <summary>
    /// Produces one vector per input text.
    /// </summary>
    /// <param name="texts">
    /// Non-null list of non-null, non-whitespace strings. An empty list is
    /// valid and short-circuits with an empty result (no network call).
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// A list whose <c>Count</c> matches <paramref name="texts"/>.<c>Count</c>
    /// and whose element at index <c>i</c> corresponds to
    /// <paramref name="texts"/>[<c>i</c>]. Vectors are raw (not pre-normalized).
    /// Downstream stores such as <c>InMemoryVectorStore</c> normalize on
    /// insert and query, so doing it here would double-normalize. Every
    /// vector produced by the same instance has the same dimensionality for
    /// the instance lifetime; dimension drift surfaces as
    /// <see cref="InvalidOperationException"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="texts"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="texts"/> contains a null or whitespace entry.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The provider returned the wrong number of vectors, an empty vector,
    /// or a vector whose length does not match a configured
    /// <c>ExpectedDimensions</c>. Transport or provider failures surface as
    /// their own exceptions; V1 does not retry.
    /// </exception>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);
}
