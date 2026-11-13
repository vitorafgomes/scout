using Microsoft.Extensions.AI;

namespace Scout.Llm.Providers.None;

/// <summary>
/// Embedding generator for the None provider: every call fails with
/// <see cref="InvalidOperationException"/> so skill and project indexing
/// degrade the way they do when a provider is unreachable.
/// </summary>
internal sealed class NoneEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("No AI provider is connected.");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
