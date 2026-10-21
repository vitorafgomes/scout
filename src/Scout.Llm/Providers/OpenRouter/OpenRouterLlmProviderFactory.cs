using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using OpenAI;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.OpenRouter;

/// <summary>
/// <see cref="ILlmProviderFactory"/> for OpenRouter. Chat runs against the preset
/// OpenRouter base URL via the OpenAI connector. OpenRouter serves no embeddings,
/// so the <see cref="OpenRouterEmbeddingOptions"/> block is required and points at
/// a separate OpenAI-compatible embeddings host with its own endpoint and key.
/// Like the OpenAI factory, this is the only OpenRouter type that touches the
/// OpenAI connector package, so the abstraction layer stays connector-free.
/// </summary>
public sealed class OpenRouterLlmProviderFactory : ILlmProviderFactory
{
    /// <summary>Preset OpenRouter base URL used for chat completions.</summary>
    private const string ChatEndpoint = "https://openrouter.ai/api/v1";

    private readonly string _chatApiKey;
    private readonly string _chatModel;
    private readonly string _embeddingApiKey;
    private readonly string _embeddingEndpoint;
    private readonly string _embeddingModel;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the
    /// OpenRouter block and throws <see cref="ArgumentException"/> on bad values.
    /// </summary>
    public OpenRouterLlmProviderFactory(IOptions<LlmOptions> options)
        : this(options, httpClient: null)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be stubbed
    /// so the HTTP boundary is exercised without reaching a live endpoint. When
    /// supplied, both SK services are registered against it.
    /// </summary>
    internal OpenRouterLlmProviderFactory(IOptions<LlmOptions> options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var openRouter = options.Value.OpenRouter;
        if (openRouter is null)
        {
            throw new ArgumentException("LlmOptions.OpenRouter must be configured.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(openRouter.ApiKey))
        {
            throw new ArgumentException(
                "OpenRouterOptions.ApiKey must be non-empty.", nameof(options));
        }

        var chat = openRouter.Chat;
        if (chat is null || string.IsNullOrWhiteSpace(chat.Model))
        {
            throw new ArgumentException(
                "OpenRouterOptions.Chat.Model must be non-empty.", nameof(options));
        }

        var embeddings = openRouter.Embeddings;
        if (embeddings is null)
        {
            throw new ArgumentException(
                "OpenRouterOptions.Embeddings must be configured.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(embeddings.Model))
        {
            throw new ArgumentException(
                "OpenRouterOptions.Embeddings.Model must be non-empty.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(embeddings.Endpoint))
        {
            throw new ArgumentException(
                "OpenRouterOptions.Embeddings.Endpoint must be non-empty.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(embeddings.ApiKey))
        {
            throw new ArgumentException(
                "OpenRouterOptions.Embeddings.ApiKey must be non-empty.", nameof(options));
        }

        ValidateEndpoint(embeddings.Endpoint, "OpenRouterOptions.Embeddings.Endpoint");

        _chatApiKey = openRouter.ApiKey;
        _chatModel = chat.Model;
        _embeddingApiKey = embeddings.ApiKey;
        _embeddingEndpoint = embeddings.Endpoint;
        _embeddingModel = embeddings.Model;
        _httpClient = httpClient;
    }

    private static void ValidateEndpoint(string endpoint, string name)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"{name} '{endpoint}' is not a valid absolute http or https URI.",
                nameof(endpoint));
        }
    }

    /// <inheritdoc/>
    public string Name => "OpenRouter";

    /// <inheritdoc/>
    public void AddToKernel(IKernelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        AddChat(builder);
        AddEmbeddings(builder);
    }

    /// <summary>
    /// Build a standalone <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>
    /// using the same connector wiring as <see cref="AddToKernel"/>, but resolved
    /// from a throwaway kernel so it carries no dependency on the shared kernel.
    /// The composition root registers the returned instance directly, breaking the
    /// embedder-to-kernel construction cycle.
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator()
    {
        var builder = Kernel.CreateBuilder();
        AddEmbeddings(builder);

        return builder.Build().GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
    }

    private OpenAIClient BuildClient(string apiKey, string endpoint)
    {
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint, UriKind.Absolute),
        };
        if (_httpClient is not null)
        {
            clientOptions.Transport = new HttpClientPipelineTransport(_httpClient);
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
    }

    // SKEXP0010: the OpenAIClient-based registration overloads are experimental.
    // The consumed type (IChatCompletionService) is stable, so the suppression is
    // scoped to the registration call only and never leaks to gateway consumers.
#pragma warning disable SKEXP0010
    private void AddChat(IKernelBuilder builder)
        => builder.AddOpenAIChatCompletion(
            modelId: _chatModel, openAIClient: BuildClient(_chatApiKey, ChatEndpoint));
#pragma warning restore SKEXP0010

    // SKEXP0010: AddOpenAIEmbeddingGenerator is experimental. The consumed type
    // (IEmbeddingGenerator<string, Embedding<float>>) is stable, so the
    // suppression is scoped to the registration call only.
#pragma warning disable SKEXP0010
    private void AddEmbeddings(IKernelBuilder builder)
        => builder.AddOpenAIEmbeddingGenerator(
            modelId: _embeddingModel,
            openAIClient: BuildClient(_embeddingApiKey, _embeddingEndpoint));
#pragma warning restore SKEXP0010
}
