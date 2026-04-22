namespace Scout.Plugin.CodeQA;

/// <summary>
/// Top-level configuration for the CodeQA plugin. Bound from
/// "Scout:Plugins:CodeQA".
/// </summary>
public sealed class CodeQAOptions
{
    public RepositoryOptions Repository { get; set; } = new();
    public EmbeddingOptions Embeddings { get; set; } = new();
    public VectorStoreOptions VectorStore { get; set; } = new();
    public RetrievalOptions Retrieval { get; set; } = new();
    public IngestionOptions Ingestion { get; set; } = new();
    public ChunkingOptions Chunking { get; set; } = new();
}

public sealed class ChunkingOptions
{
    public int ChunkTokens { get; set; } = 500;
    public int OverlapTokens { get; set; } = 50;
    public int ApproxBytesPerToken { get; set; } = 4;
}

public sealed class RepositoryOptions
{
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";

    /// <summary>
    /// Name of the environment variable carrying the read-only GitHub PAT.
    /// Required scope: <c>repo:read</c> only. Never carry the token inline.
    /// </summary>
    public string TokenEnvVar { get; set; } = "SCOUT_GITHUB_TOKEN";
}

public sealed class EmbeddingOptions
{
    public string Provider { get; set; } = "Ollama";
    public string Model { get; set; } = "nomic-embed-text";
}

public sealed class VectorStoreOptions
{
    /// <summary>"InMemory" is the V1 default. Other providers land as follow-ups.</summary>
    public string Provider { get; set; } = "InMemory";
}

public sealed class RetrievalOptions
{
    public int TopK { get; set; } = 6;
    public int MaxContextTokens { get; set; } = 4000;
}

public sealed class IngestionOptions
{
    public IList<string> IncludeGlobs { get; set; } = new List<string> { "**/*.cs", "**/*.md", "**/*.ts", "**/*.tsx" };
    public IList<string> ExcludeGlobs { get; set; } = new List<string> { "**/bin/**", "**/obj/**", "**/node_modules/**", "**/.git/**" };
    public int MaxFileBytes { get; set; } = 200_000;
}
