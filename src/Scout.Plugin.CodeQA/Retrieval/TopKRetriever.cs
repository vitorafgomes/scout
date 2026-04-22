using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Plugin.CodeQA.Embedding;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Retrieval;

/// <summary>
/// Default <see cref="IRetriever"/> implementation. Embeds the query with
/// the configured <see cref="IEmbedder"/> and delegates similarity search
/// to the registered <see cref="IVectorStore"/>.
/// </summary>
public sealed class TopKRetriever : IRetriever
{
    private readonly IOptions<CodeQAOptions> _options;
    private readonly IEmbedder _embedder;
    private readonly IVectorStore _store;
    private readonly ILogger<TopKRetriever> _logger;

    /// <summary>Creates a retriever bound to the given embedder and vector store.</summary>
    public TopKRetriever(
        IOptions<CodeQAOptions> options,
        IEmbedder embedder,
        IVectorStore store,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TopKRetriever>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorSearchResult>> RetrieveAsync(
        string query,
        int? topKOverride,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (topKOverride is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(topKOverride), "topKOverride must be positive.");
        }

        var topK = topKOverride ?? _options.Value.Retrieval.TopK;

        var vectors = await _embedder
            .EmbedAsync(new[] { query }, cancellationToken)
            .ConfigureAwait(false);

        // Embedder contract: returns exactly one vector per input text.
        var queryVector = vectors[0];

        var hits = await _store
            .SearchAsync(queryVector, topK, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Retriever returned {HitCount} hits for query (topK={TopK}).",
            hits.Count, topK);

        return hits;
    }
}
