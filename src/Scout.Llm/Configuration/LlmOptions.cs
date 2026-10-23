namespace Scout.Llm.Configuration;

/// <summary>
/// Top-level LLM configuration. Bound from the "Scout:Llm" section. The active
/// provider is selected by <see cref="Provider"/>; provider-specific blocks
/// (<see cref="Ollama"/>, <see cref="Gemini"/>, <see cref="OpenAI"/>,
/// <see cref="OpenRouter"/>, <see cref="AzureOpenAI"/>, <see cref="Mistral"/>,
/// <see cref="HuggingFace"/>, <see cref="AzureAIInference"/>,
/// <see cref="ChatGptPlan"/>) carry the settings
/// for each implementation.
/// Single source of truth for both chat and embeddings.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>Provider factory name, e.g. "Ollama". Must match
    /// <c>ILlmProviderFactory.Name</c>.</summary>
    public string Provider { get; set; } = "Ollama";

    /// <summary>Settings for the Ollama provider.</summary>
    public OllamaOptions Ollama { get; set; } = new();

    /// <summary>Settings for the Gemini provider. Null when not configured.</summary>
    public GeminiOptions? Gemini { get; set; }

    /// <summary>Settings for the OpenAI-compatible provider. Null when not configured.</summary>
    public OpenAIOptions? OpenAI { get; set; }

    /// <summary>Settings for the OpenRouter provider. Null when not configured.</summary>
    public OpenRouterOptions? OpenRouter { get; set; }

    /// <summary>Settings for the Azure OpenAI provider. Null when not configured.</summary>
    public AzureOpenAIOptions? AzureOpenAI { get; set; }

    /// <summary>Settings for the Mistral provider. Null when not configured.</summary>
    public MistralOptions? Mistral { get; set; }

    /// <summary>Settings for the HuggingFace provider. Null when not configured.</summary>
    public HuggingFaceOptions? HuggingFace { get; set; }

    /// <summary>Settings for the Azure AI Inference provider. Null when not configured.</summary>
    public AzureAIInferenceOptions? AzureAIInference { get; set; }

    /// <summary>Settings for the ChatGPT plan provider. Null when not configured.</summary>
    public ChatGptPlanOptions? ChatGptPlan { get; set; }
}

/// <summary>Settings for the ChatGPT plan provider. Chat reaches the OpenAI
/// Responses API with the access token of the user's ChatGPT plan, read from
/// <see cref="TokenFile"/> on each request. The plan offers no embeddings.</summary>
public sealed class ChatGptPlanOptions
{
    /// <summary>Path of the JSON file holding <c>access_token</c> and
    /// <c>expires_at</c>, written by the desktop after sign-in. Required.</summary>
    public string TokenFile { get; set; } = string.Empty;

    /// <summary>Chat-completion model settings.</summary>
    public ChatModelOptions Chat { get; set; } = new() { Model = "gpt-5.6-luna" };
}

/// <summary>Settings for the generic OpenAI-compatible LLM provider. When
/// <see cref="Endpoint"/> is set the connector targets an OpenAI-compatible base
/// (e.g. NVIDIA, OpenRouter, Mistral-compat); when null the default OpenAI API is
/// used. The <see cref="Embeddings"/> block is decoupled so chat and embeddings
/// can point at different endpoints and keys.</summary>
public sealed class OpenAIOptions
{
    /// <summary>API key for the chat endpoint.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Optional OpenAI-compatible base URL for chat. Null targets the
    /// default OpenAI API.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Chat-completion model settings.</summary>
    public ChatModelOptions Chat { get; set; } = new();

    /// <summary>Embedding settings; may point at a different endpoint/key than chat.</summary>
    public OpenAIEmbeddingOptions Embeddings { get; set; } = new();
}

/// <summary>Embedding settings for the OpenAI-compatible provider. When
/// <see cref="Endpoint"/> or <see cref="ApiKey"/> are set they override the chat
/// endpoint/key, so embeddings can run against a separate OpenAI-compatible host
/// (e.g. chat on OpenRouter, embeddings on OpenAI).</summary>
public sealed class OpenAIEmbeddingOptions
{
    /// <summary>Embedding model identifier, e.g. "text-embedding-3-small".</summary>
    public string Model { get; set; } = "text-embedding-3-small";

    /// <summary>Optional embeddings base URL. Null falls back to the chat endpoint.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Optional embeddings API key. Null falls back to the chat key.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Settings for the OpenRouter provider. Chat targets the preset
/// OpenRouter base URL via the OpenAI connector. OpenRouter serves no embeddings,
/// so the <see cref="Embeddings"/> block is required and points at a separate
/// OpenAI-compatible embeddings host with its own endpoint and key.</summary>
public sealed class OpenRouterOptions
{
    /// <summary>API key for the OpenRouter chat endpoint.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Chat-completion model settings.</summary>
    public ChatModelOptions Chat { get; set; } = new();

    /// <summary>Embedding settings; targets a separate OpenAI-compatible host since
    /// OpenRouter serves no embeddings. Endpoint and key are required.</summary>
    public OpenRouterEmbeddingOptions Embeddings { get; set; } = new();
}

/// <summary>Embedding settings for the OpenRouter provider. OpenRouter has no
/// embeddings endpoint, so all three fields point at a separate OpenAI-compatible
/// host and are required.</summary>
public sealed class OpenRouterEmbeddingOptions
{
    /// <summary>Embedding model identifier, e.g. "text-embedding-3-small".</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Base URL of the OpenAI-compatible embeddings host. Required.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API key for the embeddings host. Required.</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Settings for the Azure OpenAI provider. Uses deployment names rather
/// than bare model identifiers; the endpoint is required.</summary>
public sealed class AzureOpenAIOptions
{
    /// <summary>Azure OpenAI API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Azure OpenAI resource endpoint, e.g. https://my-aoai.openai.azure.com.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Chat deployment settings.</summary>
    public AzureDeploymentOptions Chat { get; set; } = new();

    /// <summary>Embedding deployment settings.</summary>
    public AzureDeploymentOptions Embeddings { get; set; } = new();
}

/// <summary>Azure OpenAI deployment settings. The deployment name is the unit the
/// connector binds against; the underlying model is configured in the portal.</summary>
public sealed class AzureDeploymentOptions
{
    /// <summary>Azure OpenAI deployment name.</summary>
    public string Deployment { get; set; } = string.Empty;
}

/// <summary>Settings for the Mistral LLM provider. One API key serves both chat
/// and embeddings; the endpoint is optional.</summary>
public sealed class MistralOptions
{
    /// <summary>Mistral API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Optional Mistral-compatible base URL. Null targets the default API.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Chat-completion model settings.</summary>
    public ChatModelOptions Chat { get; set; } = new();

    /// <summary>Embedding model settings.</summary>
    public EmbeddingModelOptions Embeddings { get; set; } = new();
}

/// <summary>Settings for the HuggingFace LLM provider. One API key serves both
/// chat and embeddings; the endpoint is optional.</summary>
public sealed class HuggingFaceOptions
{
    /// <summary>HuggingFace API token.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Optional HuggingFace inference base URL. Null targets the default API.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Chat-completion model settings.</summary>
    public ChatModelOptions Chat { get; set; } = new();

    /// <summary>Embedding model settings.</summary>
    public EmbeddingModelOptions Embeddings { get; set; } = new();
}

/// <summary>Settings for the Azure AI Inference provider. One API key serves both
/// chat and embeddings; the endpoint is required.</summary>
public sealed class AzureAIInferenceOptions
{
    /// <summary>Azure AI Inference API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Azure AI Inference endpoint URL.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Chat-completion model settings.</summary>
    public ChatModelOptions Chat { get; set; } = new();

    /// <summary>Embedding model settings.</summary>
    public EmbeddingModelOptions Embeddings { get; set; } = new();
}

/// <summary>Settings for the Google Gemini LLM provider. One API key serves both
/// the chat-completion and embedding services against the hosted Google AI API.</summary>
public sealed class GeminiOptions
{
    /// <summary>Google AI API key used for both chat and embeddings.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Chat-completion model settings.</summary>
    public ChatModelOptions Chat { get; set; } = new();

    /// <summary>Embedding model settings.</summary>
    public EmbeddingModelOptions Embeddings { get; set; } = new();
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

/// <summary>Chat-completion model settings shared across providers that bind a
/// bare model identifier.</summary>
public sealed class ChatModelOptions
{
    /// <summary>Model identifier, e.g. "llama3.1".</summary>
    public string Model { get; set; } = "llama3.1";
}

/// <summary>Embedding model settings shared across providers that bind a bare
/// model identifier.</summary>
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
