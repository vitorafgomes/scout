using Microsoft.Extensions.VectorData;

namespace Scout.Knowledge;

/// <summary>One indexed chunk of a project's code or docs.</summary>
public sealed class ChunkRecord
{
    /// <summary>Stable id: "{project}:{path}:{startLine}".</summary>
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

    /// <summary>Embedding vector; dimensions MUST match the model (nomic-embed-text = 768).</summary>
    [VectorStoreVector(Dimensions: 768, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
