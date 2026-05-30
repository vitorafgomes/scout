using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.Ollama;

/// <summary>
/// <see cref="ILlmProviderFactory"/> that wires Semantic Kernel services for
/// Ollama. Chat completion goes through the OpenAI connector against Ollama's
/// OpenAI-compatible <c>/v1</c> endpoint so the model can do function-calling;
/// embeddings stay on the native Ollama connector. This is the only type in the
/// gateway that touches those connector packages, so a future provider can be
/// added without disturbing the abstraction layer.
/// </summary>
public sealed class OllamaLlmProviderFactory : ILlmProviderFactory
{
    private readonly Uri _endpoint;
    private readonly string _chatModel;
    private readonly string _embeddingModel;
    private readonly HttpClient? _httpClient;

    public OllamaLlmProviderFactory(IOptions<LlmOptions> options)
        : this(options, httpClient: null)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be
    /// stubbed so the HTTP boundary is exercised without a real Ollama daemon.
    /// When supplied, the client's <see cref="HttpClient.BaseAddress"/> is set
    /// to the validated endpoint and both SK services are registered against it.
    /// </summary>
    internal OllamaLlmProviderFactory(IOptions<LlmOptions> options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var ollama = options.Value.Ollama;
        if (ollama is null)
        {
            throw new ArgumentException("LlmOptions.Ollama must be configured.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(ollama.Endpoint))
        {
            throw new ArgumentException(
                "OllamaOptions.Endpoint must be non-empty.", nameof(options));
        }
        if (!Uri.TryCreate(ollama.Endpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"OllamaOptions.Endpoint '{ollama.Endpoint}' is not a valid absolute " +
                "http or https URI.",
                nameof(options));
        }

        var chat = ollama.Chat;
        if (chat is null || string.IsNullOrWhiteSpace(chat.Model))
        {
            throw new ArgumentException(
                "OllamaOptions.Chat.Model must be non-empty.", nameof(options));
        }

        var embeddings = ollama.Embeddings;
        if (embeddings is null)
        {
            throw new ArgumentException(
                "OllamaOptions.Embeddings must be configured.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(embeddings.Model))
        {
            throw new ArgumentException(
                "OllamaOptions.Embeddings.Model must be non-empty.", nameof(options));
        }
        if (embeddings.MaxBatchSize < 1)
        {
            throw new ArgumentException(
                "OllamaOptions.Embeddings.MaxBatchSize must be >= 1.", nameof(options));
        }
        if (embeddings.HttpTimeoutSeconds < 1)
        {
            throw new ArgumentException(
                "OllamaOptions.Embeddings.HttpTimeoutSeconds must be >= 1.", nameof(options));
        }
        if (embeddings.ExpectedDimensions < 0)
        {
            throw new ArgumentException(
                "OllamaOptions.Embeddings.ExpectedDimensions must be >= 0.", nameof(options));
        }

        _endpoint = parsed;
        _chatModel = chat.Model;
        _embeddingModel = embeddings.Model;
        _httpClient = httpClient;
    }

    public string Name => "Ollama";

    public void AddToKernel(IKernelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (_httpClient is not null)
        {
            _httpClient.BaseAddress ??= _endpoint;
            AddChat(builder, _httpClient);
            AddEmbeddings(builder, _httpClient);
            return;
        }

        AddChat(builder, ChatEndpoint());
        AddEmbeddings(builder, _endpoint);
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
        if (_httpClient is not null)
        {
            _httpClient.BaseAddress ??= _endpoint;
            AddEmbeddings(builder, _httpClient);
        }
        else
        {
            AddEmbeddings(builder, _endpoint);
        }

        return builder.Build().GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
    }

    // Ollama exposes an OpenAI-compatible surface under "/v1". The chat service
    // talks to that base so function-calling round-trips work through the OpenAI
    // connector. Embeddings keep using the native Ollama endpoint.
    private Uri ChatEndpoint()
    {
        var trimmed = _endpoint.AbsoluteUri.TrimEnd('/');
        return new Uri($"{trimmed}/v1", UriKind.Absolute);
    }

    // SKEXP0010: AddOpenAIChatCompletion's HttpClient/endpoint overload is
    // experimental. The consumed type (IChatCompletionService) is stable, so the
    // suppression is scoped to the registration call only and never leaks to
    // consumers of the gateway.
#pragma warning disable SKEXP0010
    private void AddChat(IKernelBuilder builder, HttpClient httpClient)
        => builder.AddOpenAIChatCompletion(
            modelId: _chatModel, apiKey: "ollama", httpClient: httpClient);

    private void AddChat(IKernelBuilder builder, Uri endpoint)
        => builder.AddOpenAIChatCompletion(
            modelId: _chatModel, endpoint: endpoint, apiKey: "ollama");
#pragma warning restore SKEXP0010

    // SKEXP0070: the Ollama connector surface is experimental. The consumed
    // type (IEmbeddingGenerator<string, Embedding<float>>) is stable, so the
    // suppression is scoped to the registration call only and never leaks to
    // consumers of the gateway.
#pragma warning disable SKEXP0070
    private void AddEmbeddings(IKernelBuilder builder, HttpClient httpClient)
        => builder.AddOllamaEmbeddingGenerator(_embeddingModel, httpClient);

    private void AddEmbeddings(IKernelBuilder builder, Uri endpoint)
        => builder.AddOllamaEmbeddingGenerator(_embeddingModel, endpoint);
#pragma warning restore SKEXP0070
}
