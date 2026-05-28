namespace Scout.Llm.Configuration;

/// <summary>
/// Top-level LLM configuration. Bound from the "Scout:Llm" section. The active
/// provider is selected by <see cref="Provider"/>; provider-specific blocks
/// (<see cref="Ollama"/>, future hosted providers) carry the settings for each
/// implementation. Single source of truth for both chat and embeddings.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>Provider factory name, e.g. "Ollama". Must match
    /// <c>ILlmProviderFactory.Name</c>.</summary>
    public string Provider { get; set; } = "Ollama";

    /// <summary>Settings for the Ollama provider.</summary>
    public OllamaOptions Ollama { get; set; } = new();
}

/// <summary>Settings for the Ollama LLM provider. One endpoint serves both the
/// chat-completion and embedding services.</summary>
public sealed class OllamaOptions
{
    /// <summary>Base URL of the Ollama daemon, e.g. http://localhost:11434.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Chat-completion model settings.</summary>
    public ChatModelOptions Chat { get; set; } = new();

    /// <summary>Embedding model settings.</summary>
    public EmbeddingModelOptions Embeddings { get; set; } = new();
}

/// <summary>Chat-completion model settings for the Ollama provider.</summary>
public sealed class ChatModelOptions
{
    /// <summary>Model identifier, e.g. "llama3.1".</summary>
    public string Model { get; set; } = "llama3.1";
}

/// <summary>Embedding model settings for the Ollama provider.</summary>
public sealed class EmbeddingModelOptions
{
    /// <summary>Model identifier, e.g. "nomic-embed-text".</summary>
    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>Maximum inputs per embedding request.</summary>
    public int MaxBatchSize { get; set; } = 16;

    /// <summary>Expected vector length. Zero disables the dimension check.</summary>
    public int ExpectedDimensions { get; set; }

    /// <summary>HTTP timeout for embedding requests, in seconds.</summary>
    public int HttpTimeoutSeconds { get; set; } = 100;
}
