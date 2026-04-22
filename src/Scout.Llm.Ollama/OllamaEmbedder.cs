using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using Scout.Plugin.CodeQA;
using Scout.Plugin.CodeQA.Embedding;

namespace Scout.Llm.Ollama;

/// <summary>
/// <see cref="IEmbedder"/> backed by the
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> abstraction from
/// Microsoft.Extensions.AI, using OllamaSharp's <see cref="OllamaApiClient"/>
/// as the concrete implementation. Produces raw (un-normalized) float
/// vectors; normalization is the vector store's responsibility.
/// </summary>
/// <remarks>
/// <para>
/// The Semantic Kernel Ollama connector package is still marked experimental
/// (SKEXP0070). The specific surface used here is the stable
/// <c>Microsoft.Extensions.AI</c> embedding abstraction, which
/// <see cref="OllamaApiClient"/> implements directly, so no SK experimental
/// APIs are invoked from user code.
/// </para>
/// <para>
/// <see cref="OllamaApiClient"/> owns the HTTP client it builds internally
/// from the endpoint <see cref="Uri"/>, so this class does not dispose it.
/// The first request after a fresh process may take longer than subsequent
/// ones because Ollama pulls the model on first use; size
/// <c>HttpTimeoutSeconds</c> accordingly.
/// </para>
/// <para>
/// Inputs larger than <c>MaxBatchSize</c> are split into contiguous batches
/// and sent sequentially; the output preserves input order end-to-end.
/// </para>
/// </remarks>
public sealed class OllamaEmbedder : IEmbedder
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly string _model;
    private readonly int _maxBatchSize;
    private readonly int _expectedDimensions;

    /// <summary>
    /// Creates an embedder that owns a fresh <see cref="OllamaApiClient"/>
    /// pointed at <c>Embeddings.BaseUrl</c>.
    /// </summary>
    public OllamaEmbedder(
        IOptions<CodeQAOptions> options,
        ILoggerFactory? loggerFactory = null)
        : this(options, BuildGenerator(options), loggerFactory)
    {
    }

    /// <summary>
    /// Test seam. Accepts a pre-built embedding generator so tests can mock
    /// the boundary without going through <see cref="Uri"/> construction.
    /// </summary>
    internal OllamaEmbedder(
        IOptions<CodeQAOptions> options,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(generator);
        _ = loggerFactory; // reserved for future structured logging
        var embeddings = ValidateOptions(options);

        _generator = generator;
        _model = embeddings.Model;
        _maxBatchSize = embeddings.MaxBatchSize;
        _expectedDimensions = embeddings.ExpectedDimensions;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        for (var i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i]))
            {
                throw new ArgumentException(
                    $"Entry at index {i} is null or whitespace.", nameof(texts));
            }
        }

        if (texts.Count == 0)
        {
            return Array.Empty<ReadOnlyMemory<float>>();
        }

        var result = new List<ReadOnlyMemory<float>>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += _maxBatchSize)
        {
            var remaining = texts.Count - offset;
            var size = remaining < _maxBatchSize ? remaining : _maxBatchSize;
            var batch = new List<string>(size);
            for (var i = 0; i < size; i++)
            {
                batch.Add(texts[offset + i]);
            }

            var response = await _generator
                .GenerateAsync(batch, options: null, cancellationToken)
                .ConfigureAwait(false);

            ValidateBatchResponse(batch.Count, response);
            foreach (var embedding in response)
            {
                result.Add(embedding.Vector);
            }
        }

        return result.AsReadOnly();
    }

    private void ValidateBatchResponse(
        int inputCount,
        GeneratedEmbeddings<Embedding<float>> response)
    {
        if (response is null || response.Count != inputCount)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Ollama embedder returned {0} vectors for {1} inputs (model={2}).",
                response?.Count ?? 0,
                inputCount,
                _model));
        }

        for (var i = 0; i < response.Count; i++)
        {
            var vec = response[i].Vector;
            if (vec.IsEmpty)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Ollama embedder returned an empty vector at index {0} (model={1}).",
                    i,
                    _model));
            }

            if (_expectedDimensions > 0 && vec.Length != _expectedDimensions)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Ollama embedder returned a vector of length {0} at index {1}; " +
                    "expected {2} (model={3}). This usually means the embedding model " +
                    "changed since the vector store was populated.",
                    vec.Length,
                    i,
                    _expectedDimensions,
                    _model));
            }
        }
    }

    private static IEmbeddingGenerator<string, Embedding<float>> BuildGenerator(
        IOptions<CodeQAOptions> options)
    {
        var embeddings = ValidateOptions(options);
        var endpoint = new Uri(embeddings.BaseUrl, UriKind.Absolute);
        // OllamaApiClient implements IEmbeddingGenerator<string, Embedding<float>>
        // via explicit interface implementation, so the cast is required.
        return new OllamaApiClient(endpoint, embeddings.Model);
    }

    private static EmbeddingOptions ValidateOptions(IOptions<CodeQAOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException(
                "CodeQAOptions must be configured.", nameof(options));
        }

        var embeddings = options.Value.Embeddings;
        if (embeddings is null)
        {
            throw new ArgumentException(
                "CodeQAOptions.Embeddings must be configured.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(embeddings.Model))
        {
            throw new ArgumentException(
                "EmbeddingOptions.Model must be non-empty.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(embeddings.BaseUrl))
        {
            throw new ArgumentException(
                "EmbeddingOptions.BaseUrl must be non-empty.", nameof(options));
        }

        if (!Uri.TryCreate(embeddings.BaseUrl, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"EmbeddingOptions.BaseUrl '{embeddings.BaseUrl}' is not a valid absolute " +
                "http or https URI.",
                nameof(options));
        }

        if (embeddings.HttpTimeoutSeconds < 1)
        {
            throw new ArgumentException(
                "EmbeddingOptions.HttpTimeoutSeconds must be >= 1.", nameof(options));
        }

        if (embeddings.MaxBatchSize < 1)
        {
            throw new ArgumentException(
                "EmbeddingOptions.MaxBatchSize must be >= 1.", nameof(options));
        }

        if (embeddings.ExpectedDimensions < 0)
        {
            throw new ArgumentException(
                "EmbeddingOptions.ExpectedDimensions must be >= 0.", nameof(options));
        }

        return embeddings;
    }
}
