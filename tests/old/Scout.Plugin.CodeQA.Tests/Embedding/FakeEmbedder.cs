using Scout.Plugin.CodeQA.Embedding;

namespace Scout.Plugin.CodeQA.Tests.Embedding;

/// <summary>
/// Deterministic <see cref="IEmbedder"/> for tests. Same input string always
/// yields the same vector; different inputs yield different vectors. No
/// network calls, no dependencies beyond the BCL.
/// </summary>
internal sealed class FakeEmbedder : IEmbedder
{
    private readonly int _dimensions;

    public FakeEmbedder(int dimensions = 8)
    {
        if (dimensions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        }
        _dimensions = dimensions;
    }

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
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

        cancellationToken.ThrowIfCancellationRequested();

        if (texts.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                Array.Empty<ReadOnlyMemory<float>>());
        }

        var result = new List<ReadOnlyMemory<float>>(texts.Count);
        foreach (var text in texts)
        {
            result.Add(Embed(text));
        }

        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(result.AsReadOnly());
    }

    private ReadOnlyMemory<float> Embed(string text)
    {
        // Derive a stable 32-bit hash from the ordinal bytes so the output
        // does not depend on runtime-randomized String.GetHashCode.
        var hash = StableHash(text);
        var vec = new float[_dimensions];
        for (var i = 0; i < _dimensions; i++)
        {
            var byteVal = (hash >> (i % 31)) & 0xFF;
            vec[i] = (byteVal / 255f) - 0.5f;
        }
        return vec;
    }

    private static uint StableHash(string text)
    {
        // FNV-1a 32-bit. Deterministic across processes and .NET versions.
        const uint offset = 2166136261u;
        const uint prime = 16777619u;
        var hash = offset;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= prime;
        }
        return hash;
    }
}
