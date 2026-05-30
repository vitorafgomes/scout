using Microsoft.Extensions.AI;

namespace Scout.Skills.Tests;

/// <summary>
/// Hand-written embedder fake: maps known input strings to fixed vectors and
/// counts batch (<see cref="GenerateAsync"/>) invocations so tests can assert
/// the skill index is embedded exactly once and lazily.
/// </summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IReadOnlyDictionary<string, float[]> _vectors;
    private readonly bool _throwOnGenerate;

    public FakeEmbeddingGenerator(
        IReadOnlyDictionary<string, float[]> vectors,
        bool throwOnGenerate = false)
    {
        _vectors = vectors;
        _throwOnGenerate = throwOnGenerate;
    }

    /// <summary>Number of times the batch <see cref="GenerateAsync"/> was invoked.</summary>
    public int BatchCallCount { get; private set; }

    /// <summary>
    /// Number of times a multi-item batch (the description index build) was
    /// invoked. Single-item batches (query embeddings) are excluded.
    /// </summary>
    public int IndexBatchCallCount { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        BatchCallCount++;
        var materialized = values as IReadOnlyCollection<string> ?? values.ToList();
        if (materialized.Count > 1)
        {
            IndexBatchCallCount++;
        }

        if (_throwOnGenerate)
        {
            throw new InvalidOperationException("embedder failure");
        }

        var result = new GeneratedEmbeddings<Embedding<float>>(
            materialized.Select(v => new Embedding<float>(Resolve(v))));
        return Task.FromResult(result);
    }

    private float[] Resolve(string value)
        => _vectors.TryGetValue(value, out var vector)
            ? vector
            : throw new KeyNotFoundException($"No fake vector for '{value}'.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
