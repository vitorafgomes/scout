using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using OpenAI;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.OpenAI;

namespace Scout.Llm.Providers.Mistral;

/// <summary>
/// <see cref="ILlmProviderFactory"/> for Mistral. Chat and embeddings both go through the OpenAI connector against
/// Mistral's OpenAI-compatible API, keyed by an API key, at <c>https://api.mistral.ai/v1</c> or a configured endpoint.
/// This is the only Mistral type that touches the OpenAI connector package, so the abstraction layer stays
/// connector-free.
/// </summary>
public sealed class MistralLlmProviderFactory : ILlmProviderFactory
{
    private const string DefaultEndpoint = "https://api.mistral.ai/v1";

    private readonly string _apiKey;
    private readonly Uri _endpoint;
    private readonly string _chatModel;
    private readonly string _embeddingModel;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the
    /// Mistral block and throws <see cref="ArgumentException"/> on bad values; an
    /// endpoint that sends the key over plain http to another host is logged as a warning.
    /// </summary>
    public MistralLlmProviderFactory(IOptions<LlmOptions> options, ILogger<MistralLlmProviderFactory>? logger = null)
        : this(options, httpClient: null, logger)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be stubbed
    /// so the HTTP boundary is exercised without reaching the live Mistral API.
    /// When supplied, both SK services are registered against it.
    /// </summary>
    internal MistralLlmProviderFactory(
        IOptions<LlmOptions> options, HttpClient? httpClient, ILogger<MistralLlmProviderFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var mistral = options.Value.Mistral ?? throw new ArgumentException("LlmOptions.Mistral must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(mistral.ApiKey))
        {
            throw new ArgumentException(
                "MistralOptions.ApiKey must be non-empty.", nameof(options));
        }

        var chat = mistral.Chat;
        if (chat is null || string.IsNullOrWhiteSpace(chat.Model))
        {
            throw new ArgumentException(
                "MistralOptions.Chat.Model must be non-empty.", nameof(options));
        }

        var embeddings = mistral.Embeddings;
        if (embeddings is null || string.IsNullOrWhiteSpace(embeddings.Model))
        {
            throw new ArgumentException(
                "MistralOptions.Embeddings.Model must be non-empty.", nameof(options));
        }

        Uri? endpoint = null;
        if (mistral.Endpoint is not null)
        {
            if (string.IsNullOrWhiteSpace(mistral.Endpoint) ||
                !Uri.TryCreate(mistral.Endpoint, UriKind.Absolute, out endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException(
                    $"MistralOptions.Endpoint '{PlainHttpEndpoint.Shown(mistral.Endpoint)}' is not a valid absolute " +
                    "http or https URI.",
                    nameof(options));
            }

            PlainHttpEndpoint.WarnIfKeyLeavesInClear(logger, Name, endpoint);
        }

        _apiKey = mistral.ApiKey;
        _endpoint = endpoint ?? new Uri(DefaultEndpoint, UriKind.Absolute);
        _chatModel = chat.Model;
        _embeddingModel = embeddings.Model;
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public string Name => "Mistral";

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

    private OpenAIClient BuildClient()
    {
        var clientOptions = new OpenAIClientOptions { Endpoint = _endpoint };
        if (_httpClient is not null)
        {
            clientOptions.Transport = new HttpClientPipelineTransport(_httpClient);
        }

        return new OpenAIClient(new ApiKeyCredential(_apiKey), clientOptions);
    }

#pragma warning disable SKEXP0010
    private void AddChat(IKernelBuilder builder)
        => builder.AddOpenAIChatCompletion(modelId: _chatModel, openAIClient: BuildClient());

    private void AddEmbeddings(IKernelBuilder builder)
        => builder.AddOpenAIEmbeddingGenerator(
            modelId: _embeddingModel,
            openAIClient: BuildClient(),
            dimensions: OpenAIEmbeddingDimensions.For(_embeddingModel));
#pragma warning restore SKEXP0010
}
