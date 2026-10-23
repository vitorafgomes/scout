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
    private readonly TaskCompletionSource _indexBatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _batchCallCount;
    private int _indexBatchCallCount;
    private int _indexFailuresRemaining;

    public FakeEmbeddingGenerator(
        IReadOnlyDictionary<string, float[]> vectors,
        bool throwOnGenerate = false)
    {
        _vectors = vectors;
        _throwOnGenerate = throwOnGenerate;
    }

    /// <summary>Number of times the batch <see cref="GenerateAsync"/> was invoked.</summary>
    public int BatchCallCount => Volatile.Read(ref _batchCallCount);

    /// <summary>
    /// Number of times a multi-item batch (the description index build) was
    /// invoked. Single-item batches (query embeddings) are excluded.
    /// </summary>
    public int IndexBatchCallCount => Volatile.Read(ref _indexBatchCallCount);

    /// <summary>Number of upcoming index batches that throw before the fake starts answering.</summary>
    public int IndexFailuresRemaining
    {
        get => Volatile.Read(ref _indexFailuresRemaining);
        set => Volatile.Write(ref _indexFailuresRemaining, value);
    }

    /// <summary>The exception a failing index batch throws.</summary>
    public Exception IndexFailure { get; set; } = new HttpRequestException("429 Too Many Requests");

    /// <summary>When set, an index batch waits on this task before answering.</summary>
    public Task? IndexGate { get; set; }

    /// <summary>Completes when the first index batch has started.</summary>
    public Task IndexBatchEntered => _indexBatchEntered.Task;

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _batchCallCount);
        var materialized = values as IReadOnlyCollection<string> ?? values.ToList();
        if (materialized.Count > 1)
        {
            Interlocked.Increment(ref _indexBatchCallCount);
            _indexBatchEntered.TrySetResult();
            if (IndexGate is { } gate)
            {
                await gate.ConfigureAwait(false);
            }

            if (Interlocked.Decrement(ref _indexFailuresRemaining) >= 0)
            {
                throw IndexFailure;
            }
        }

        if (_throwOnGenerate)
        {
            throw new InvalidOperationException("embedder failure");
        }

        return new GeneratedEmbeddings<Embedding<float>>(
            materialized.Select(v => new Embedding<float>(Resolve(v))));
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
