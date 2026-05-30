using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.HuggingFace;

/// <summary>
/// <see cref="ILlmProviderFactory"/> that wires Semantic Kernel services for
/// HuggingFace. Chat and embeddings both go through the native HuggingFace
/// connector, keyed by an API token against an optional custom endpoint. Note:
/// function-calling support depends on the chosen model and is a runtime concern.
/// This is the only type in the gateway that touches the HuggingFace connector
/// package, so the abstraction layer stays connector-free.
/// </summary>
public sealed class HuggingFaceLlmProviderFactory : ILlmProviderFactory
{
    private readonly string _apiKey;
    private readonly Uri? _endpoint;
    private readonly string _chatModel;
    private readonly string _embeddingModel;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the
    /// HuggingFace block and throws <see cref="ArgumentException"/> on bad values.
    /// </summary>
    public HuggingFaceLlmProviderFactory(IOptions<LlmOptions> options)
        : this(options, httpClient: null)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be stubbed
    /// so the HTTP boundary is exercised without reaching the live HuggingFace API.
    /// When supplied, both SK services are registered against it.
    /// </summary>
    internal HuggingFaceLlmProviderFactory(IOptions<LlmOptions> options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var huggingFace = options.Value.HuggingFace;
        if (huggingFace is null)
        {
            throw new ArgumentException("LlmOptions.HuggingFace must be configured.", nameof(options));
        }

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
                    $"HuggingFaceOptions.Endpoint '{huggingFace.Endpoint}' is not a valid " +
                    "absolute http or https URI.",
                    nameof(options));
            }
        }

        _apiKey = huggingFace.ApiKey;
        _endpoint = endpoint;
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

    // SKEXP0070: the HuggingFace connector surface is experimental. The consumed
    // type (IChatCompletionService) is stable, so the suppression is scoped to the
    // registration call only and never leaks to gateway consumers.
#pragma warning disable SKEXP0070
    private void AddChat(IKernelBuilder builder)
        => builder.AddHuggingFaceChatCompletion(
            model: _chatModel, endpoint: _endpoint, apiKey: _apiKey, httpClient: _httpClient);
#pragma warning restore SKEXP0070

    // SKEXP0070: the HuggingFace connector surface is experimental. The consumed
    // type (IEmbeddingGenerator<string, Embedding<float>>) is stable, so the
    // suppression is scoped to the registration call only.
#pragma warning disable SKEXP0070
    private void AddEmbeddings(IKernelBuilder builder)
        => builder.AddHuggingFaceEmbeddingGenerator(
            model: _embeddingModel, endpoint: _endpoint, apiKey: _apiKey, httpClient: _httpClient);
#pragma warning restore SKEXP0070
}
