using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Retrieval;

/// <summary>
/// Finds the chunks most relevant to a natural-language query. The retriever
/// is the only component the composer talks to for grounding, which keeps
/// the embedding/vector-store pair swappable behind a single seam.
/// </summary>
public interface IRetriever
{
    /// <summary>
    /// Embeds the query and returns the top matches from the vector store in
    /// descending similarity order.
    /// </summary>
    /// <param name="query">Non-null, non-whitespace query text.</param>
    /// <param name="topKOverride">
    /// Optional per-call override for top-K. <c>null</c> uses the configured
    /// default; values must be positive.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<IReadOnlyList<VectorSearchResult>> RetrieveAsync(
        string query,
        int? topKOverride,
        CancellationToken cancellationToken);
}
