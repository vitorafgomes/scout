using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.OpenAI;

namespace Scout.Llm.Providers.OpenRouter;

/// <summary>
/// <see cref="ILlmProviderFactory"/> for OpenRouter. Chat runs against the preset
/// OpenRouter base URL via the OpenAI connector. OpenRouter serves no embeddings,
/// so the <see cref="OpenRouterEmbeddingOptions"/> block points at a separate
/// OpenAI-compatible embeddings host with its own endpoint and key; left empty, as
/// the desktop leaves it, the provider has no embeddings and every embedding call
/// fails with <see cref="NotSupportedException"/>.
/// Like the OpenAI factory, this is the only OpenRouter type that touches the
/// OpenAI connector package, so the abstraction layer stays connector-free.
/// </summary>
public sealed class OpenRouterLlmProviderFactory : ILlmProviderFactory
{
    /// <summary>Preset OpenRouter base URL used for chat completions.</summary>
    private const string ChatEndpoint = "https://openrouter.ai/api/v1";

    private readonly string _chatApiKey;
    private readonly string _chatModel;
    private readonly string? _embeddingApiKey;
    private readonly string? _embeddingEndpoint;
    private readonly string? _embeddingModel;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the
    /// OpenRouter block and throws <see cref="ArgumentException"/> on bad values; an
    /// embeddings endpoint that sends the key over plain http to another host is logged as a warning.
    /// </summary>
    public OpenRouterLlmProviderFactory(IOptions<LlmOptions> options, ILogger<OpenRouterLlmProviderFactory>? logger = null)
        : this(options, httpClient: null, logger)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be stubbed
    /// so the HTTP boundary is exercised without reaching a live endpoint. When
    /// supplied, both SK services are registered against it.
    /// </summary>
    internal OpenRouterLlmProviderFactory(
        IOptions<LlmOptions> options, HttpClient? httpClient, ILogger<OpenRouterLlmProviderFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var openRouter = options.Value.OpenRouter ?? throw new ArgumentException("LlmOptions.OpenRouter must be configured.", nameof(options));
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

        var embeddings = openRouter.Embeddings ?? throw new ArgumentException(
                "OpenRouterOptions.Embeddings must be configured.", nameof(options));
        _chatApiKey = openRouter.ApiKey;
        _chatModel = chat.Model;
        _httpClient = httpClient;
        if (string.IsNullOrWhiteSpace(embeddings.Model)
            && string.IsNullOrWhiteSpace(embeddings.Endpoint)
            && string.IsNullOrWhiteSpace(embeddings.ApiKey))
        {
            return;
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

        ValidateEndpoint(embeddings.Endpoint, "OpenRouterOptions.Embeddings.Endpoint", logger);

        _embeddingApiKey = embeddings.ApiKey;
        _embeddingEndpoint = embeddings.Endpoint;
        _embeddingModel = embeddings.Model;
    }

    private void ValidateEndpoint(string endpoint, string name, ILogger? logger)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"{name} '{PlainHttpEndpoint.Shown(endpoint)}' is not a valid absolute http or https URI.",
                nameof(endpoint));
        }

        PlainHttpEndpoint.WarnIfKeyLeavesInClear(logger, Name, parsed);
    }

    /// <inheritdoc/>
    public string Name => "OpenRouter";

    /// <inheritdoc/>
    public MediaCapabilities Media => new(false, false, Name) { Vision = true };

    /// <inheritdoc/>
    public void AddToKernel(IKernelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        AddChat(builder, _chatModel);
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
        if (_embeddingModel is null || _embeddingApiKey is null || _embeddingEndpoint is null)
        {
            return new NoEmbeddingGenerator();
        }

        var builder = Kernel.CreateBuilder();
        AddEmbeddings(builder);

        return builder.Build().GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
    }

    /// <inheritdoc/>
    public IChatCompletionService CreateChatCompletionService(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var builder = Kernel.CreateBuilder();
        AddChat(builder, model);

        return builder.Build().GetRequiredService<IChatCompletionService>();
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
    private void AddChat(IKernelBuilder builder, string model)
        => builder.AddOpenAIChatCompletion(
            modelId: model, openAIClient: BuildClient(_chatApiKey, ChatEndpoint));
#pragma warning restore SKEXP0010

    // SKEXP0010: AddOpenAIEmbeddingGenerator is experimental. The consumed type
    // (IEmbeddingGenerator<string, Embedding<float>>) is stable, so the
    // suppression is scoped to the registration call only.
#pragma warning disable SKEXP0010
    private void AddEmbeddings(IKernelBuilder builder)
    {
        if (_embeddingModel is null || _embeddingApiKey is null || _embeddingEndpoint is null)
        {
            builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new NoEmbeddingGenerator());
            return;
        }

        builder.AddOpenAIEmbeddingGenerator(
            modelId: _embeddingModel,
            openAIClient: BuildClient(_embeddingApiKey, _embeddingEndpoint),
            dimensions: OpenAIEmbeddingDimensions.For(_embeddingModel));
    }
#pragma warning restore SKEXP0010

    private sealed class NoEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                new NotSupportedException("Embeddings are not available with the OpenRouter provider when no embeddings host is set."));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
