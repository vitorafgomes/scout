using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.AzureAIInference;

/// <summary>
/// <see cref="ILlmProviderFactory"/> that wires Semantic Kernel services for
/// Azure AI Inference. Chat and embeddings both go through the native Azure AI
/// Inference connector, keyed by an API key against a required endpoint. This is
/// the only type in the gateway that touches the connector package, so the
/// abstraction layer stays connector-free.
/// </summary>
public sealed class AzureAIInferenceLlmProviderFactory : ILlmProviderFactory
{
    private readonly string _apiKey;
    private readonly Uri _endpoint;
    private readonly string _chatModel;
    private readonly string _embeddingModel;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the
    /// AzureAIInference block and throws <see cref="ArgumentException"/> on bad values.
    /// </summary>
    public AzureAIInferenceLlmProviderFactory(IOptions<LlmOptions> options)
        : this(options, httpClient: null)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be stubbed
    /// so the HTTP boundary is exercised without reaching the live endpoint. When
    /// supplied, both SK services are registered against it.
    /// </summary>
    internal AzureAIInferenceLlmProviderFactory(IOptions<LlmOptions> options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var inference = options.Value.AzureAIInference;
        if (inference is null)
        {
            throw new ArgumentException(
                "LlmOptions.AzureAIInference must be configured.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(inference.ApiKey))
        {
            throw new ArgumentException(
                "AzureAIInferenceOptions.ApiKey must be non-empty.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(inference.Endpoint))
        {
            throw new ArgumentException(
                "AzureAIInferenceOptions.Endpoint must be non-empty.", nameof(options));
        }
        if (!Uri.TryCreate(inference.Endpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"AzureAIInferenceOptions.Endpoint '{inference.Endpoint}' is not a valid " +
                "absolute http or https URI.",
                nameof(options));
        }

        var chat = inference.Chat;
        if (chat is null || string.IsNullOrWhiteSpace(chat.Model))
        {
            throw new ArgumentException(
                "AzureAIInferenceOptions.Chat.Model must be non-empty.", nameof(options));
        }

        var embeddings = inference.Embeddings;
        if (embeddings is null || string.IsNullOrWhiteSpace(embeddings.Model))
        {
            throw new ArgumentException(
                "AzureAIInferenceOptions.Embeddings.Model must be non-empty.", nameof(options));
        }

        _apiKey = inference.ApiKey;
        _endpoint = parsed;
        _chatModel = chat.Model;
        _embeddingModel = embeddings.Model;
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public string Name => "AzureAIInference";

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

    // SKEXP0070: the Azure AI Inference connector surface is experimental. The
    // consumed type (IChatCompletionService) is stable, so the suppression is
    // scoped to the registration call only and never leaks to gateway consumers.
#pragma warning disable SKEXP0070
    private void AddChat(IKernelBuilder builder)
        => builder.AddAzureAIInferenceChatCompletion(
            modelId: _chatModel, apiKey: _apiKey, endpoint: _endpoint, httpClient: _httpClient);
#pragma warning restore SKEXP0070

    // SKEXP0070: the Azure AI Inference connector surface is experimental. The
    // embedding generator is registered on the builder's service collection (the
    // connector exposes no IKernelBuilder overload). The consumed type
    // (IEmbeddingGenerator<string, Embedding<float>>) is stable, so the
    // suppression is scoped to the registration call only.
#pragma warning disable SKEXP0070
    private void AddEmbeddings(IKernelBuilder builder)
        => builder.Services.AddAzureAIInferenceEmbeddingGenerator(
            modelId: _embeddingModel, apiKey: _apiKey, endpoint: _endpoint, httpClient: _httpClient);
#pragma warning restore SKEXP0070
}
