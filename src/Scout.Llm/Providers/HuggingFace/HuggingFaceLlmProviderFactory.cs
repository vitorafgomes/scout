using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using OpenAI;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.HuggingFace;

/// <summary>
/// <see cref="ILlmProviderFactory"/> for HuggingFace, keyed by an API token at <c>https://api-inference.huggingface.co</c>
/// or a configured endpoint. Chat goes through the OpenAI connector against the endpoint's <c>v1</c> path, the URL the
/// HuggingFace connector posted chat to; embeddings stay on the HuggingFace connector, whose feature-extraction pipeline
/// is not the OpenAI embeddings API. This is the only type that touches the HuggingFace connector package, so the
/// abstraction layer stays connector-free.
/// </summary>
public sealed class HuggingFaceLlmProviderFactory : ILlmProviderFactory
{
    private const string DefaultEndpoint = "https://api-inference.huggingface.co";

    private readonly string _apiKey;
    private readonly Uri? _endpoint;
    private readonly Uri _chatEndpoint;
    private readonly string _chatModel;
    private readonly string _embeddingModel;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the
    /// HuggingFace block and throws <see cref="ArgumentException"/> on bad values; an
    /// endpoint that sends the key over plain http to another host is logged as a warning.
    /// </summary>
    public HuggingFaceLlmProviderFactory(IOptions<LlmOptions> options, ILogger<HuggingFaceLlmProviderFactory>? logger = null)
        : this(options, httpClient: null, logger)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be stubbed
    /// so the HTTP boundary is exercised without reaching the live HuggingFace API.
    /// When supplied, both SK services are registered against it.
    /// </summary>
    internal HuggingFaceLlmProviderFactory(
        IOptions<LlmOptions> options, HttpClient? httpClient, ILogger<HuggingFaceLlmProviderFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var huggingFace = options.Value.HuggingFace ?? throw new ArgumentException("LlmOptions.HuggingFace must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(huggingFace.ApiKey))
        {
            throw new ArgumentException(
                "HuggingFaceOptions.ApiKey must be non-empty.", nameof(options));
        }

        var chat = huggingFace.Chat;
        if (chat is null || string.IsNullOrWhiteSpace(chat.Model))
        {
            throw new ArgumentException(
                "HuggingFaceOptions.Chat.Model must be non-empty.", nameof(options));
        }

        var embeddings = huggingFace.Embeddings;
        if (embeddings is null || string.IsNullOrWhiteSpace(embeddings.Model))
        {
            throw new ArgumentException(
                "HuggingFaceOptions.Embeddings.Model must be non-empty.", nameof(options));
        }

        Uri? endpoint = null;
        if (huggingFace.Endpoint is not null)
        {
            if (string.IsNullOrWhiteSpace(huggingFace.Endpoint) ||
                !Uri.TryCreate(huggingFace.Endpoint, UriKind.Absolute, out endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException(
                    $"HuggingFaceOptions.Endpoint '{PlainHttpEndpoint.Shown(huggingFace.Endpoint)}' is not a valid " +
                    "absolute http or https URI.",
                    nameof(options));
            }

            PlainHttpEndpoint.WarnIfKeyLeavesInClear(logger, Name, endpoint);
        }

        _apiKey = huggingFace.ApiKey;
        _endpoint = endpoint;
        _chatEndpoint = ChatEndpoint(endpoint ?? new Uri(DefaultEndpoint, UriKind.Absolute));
        _chatModel = chat.Model;
        _embeddingModel = embeddings.Model;
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public string Name => "HuggingFace";

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

    private static Uri ChatEndpoint(Uri endpoint)
        => new($"{endpoint}{(endpoint.AbsolutePath.EndsWith('/') ? string.Empty : "/")}v1", UriKind.Absolute);

    private void AddChat(IKernelBuilder builder)
    {
        var clientOptions = new OpenAIClientOptions { Endpoint = _chatEndpoint };
        if (_httpClient is not null)
        {
            clientOptions.Transport = new HttpClientPipelineTransport(_httpClient);
        }

        builder.AddOpenAIChatCompletion(
            modelId: _chatModel, openAIClient: new OpenAIClient(new ApiKeyCredential(_apiKey), clientOptions));
    }

#pragma warning disable SKEXP0070
    private void AddEmbeddings(IKernelBuilder builder)
        => builder.AddHuggingFaceEmbeddingGenerator(
            model: _embeddingModel, endpoint: _endpoint, apiKey: _apiKey, httpClient: _httpClient);
#pragma warning restore SKEXP0070
}
