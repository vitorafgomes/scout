using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.AzureOpenAI;

/// <summary>
/// <see cref="ILlmProviderFactory"/> that wires Semantic Kernel services for
/// Azure OpenAI. Chat and embeddings bind against deployment names on a required
/// resource endpoint. This is the only type in the gateway that touches the Azure
/// OpenAI connector package, so the abstraction layer stays connector-free.
/// </summary>
public sealed class AzureOpenAILlmProviderFactory : ILlmProviderFactory
{
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _chatDeployment;
    private readonly string _embeddingDeployment;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the
    /// AzureOpenAI block and throws <see cref="ArgumentException"/> on bad values.
    /// </summary>
    public AzureOpenAILlmProviderFactory(IOptions<LlmOptions> options)
        : this(options, httpClient: null)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be stubbed
    /// so the HTTP boundary is exercised without reaching a live Azure endpoint.
    /// When supplied, both SK services are registered against it.
    /// </summary>
    internal AzureOpenAILlmProviderFactory(IOptions<LlmOptions> options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var azure = options.Value.AzureOpenAI;
        if (azure is null)
        {
            throw new ArgumentException("LlmOptions.AzureOpenAI must be configured.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(azure.ApiKey))
        {
            throw new ArgumentException(
                "AzureOpenAIOptions.ApiKey must be non-empty.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(azure.Endpoint))
        {
            throw new ArgumentException(
                "AzureOpenAIOptions.Endpoint must be non-empty.", nameof(options));
        }
        if (!Uri.TryCreate(azure.Endpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"AzureOpenAIOptions.Endpoint '{azure.Endpoint}' is not a valid absolute " +
                "http or https URI.",
                nameof(options));
        }

        var chat = azure.Chat;
        if (chat is null || string.IsNullOrWhiteSpace(chat.Deployment))
        {
            throw new ArgumentException(
                "AzureOpenAIOptions.Chat.Deployment must be non-empty.", nameof(options));
        }

        var embeddings = azure.Embeddings;
        if (embeddings is null || string.IsNullOrWhiteSpace(embeddings.Deployment))
        {
            throw new ArgumentException(
                "AzureOpenAIOptions.Embeddings.Deployment must be non-empty.", nameof(options));
        }

        _apiKey = azure.ApiKey;
        _endpoint = azure.Endpoint;
        _chatDeployment = chat.Deployment;
        _embeddingDeployment = embeddings.Deployment;
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public string Name => "AzureOpenAI";

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

    private void AddChat(IKernelBuilder builder)
        => builder.AddAzureOpenAIChatCompletion(
            deploymentName: _chatDeployment,
            endpoint: _endpoint,
            apiKey: _apiKey,
            httpClient: _httpClient);

    // SKEXP0010: AddAzureOpenAIEmbeddingGenerator is experimental. The consumed
    // type (IEmbeddingGenerator<string, Embedding<float>>) is stable, so the
    // suppression is scoped to the registration call only.
#pragma warning disable SKEXP0010
    private void AddEmbeddings(IKernelBuilder builder)
        => builder.AddAzureOpenAIEmbeddingGenerator(
            deploymentName: _embeddingDeployment,
            endpoint: _endpoint,
            apiKey: _apiKey,
            httpClient: _httpClient);
#pragma warning restore SKEXP0010
}
