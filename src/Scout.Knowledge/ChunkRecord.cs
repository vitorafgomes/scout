using Microsoft.Extensions.VectorData;

namespace Scout.Knowledge;

/// <summary>One indexed chunk of a project's code or docs.</summary>
public sealed class ChunkRecord
{
    /// <summary>
    /// The chunk's key: "{project}:{path}:{startLine}:{ordinal}", the ordinal being the chunk's position among its
    /// file's chunks, so two declarations that start on one line never share a key.
    /// </summary>
    [VectorStoreKey]
    public string Id { get; set; } = default!;

    /// <summary>Project name; filterable for per-project retrieval.</summary>
    [VectorStoreData(IsIndexed = true)]
    public string Project { get; set; } = default!;

    /// <summary>Repo-relative file path.</summary>
    [VectorStoreData]
    public string Path { get; set; } = default!;

    /// <summary>"code" or "doc".</summary>
    [VectorStoreData]
    public string Kind { get; set; } = default!;

    /// <summary>Symbol name (method/class) or empty for line chunks.</summary>
    [VectorStoreData]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>1-based inclusive start line.</summary>
    [VectorStoreData]
    public int StartLine { get; set; }

    /// <summary>1-based inclusive end line.</summary>
    [VectorStoreData]
    public int EndLine { get; set; }

    /// <summary>The chunk text.</summary>
    [VectorStoreData]
    public string Content { get; set; } = default!;

    /// <summary>The number of values the index stores per vector; a vector of another size is never stored.</summary>
    public const int EmbeddingDimensions = 768;

    /// <summary>Embedding vector of <see cref="EmbeddingDimensions"/> values, or empty when the chunk is indexed without one.</summary>
    [VectorStoreVector(EmbeddingDimensions, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
