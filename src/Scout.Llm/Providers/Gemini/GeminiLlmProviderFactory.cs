using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.Gemini;

/// <summary>
/// <see cref="ILlmProviderFactory"/> that wires Semantic Kernel services for
/// Google Gemini. Chat completion and embeddings both go through the native
/// Google AI connector against the hosted Generative Language API, keyed by an
/// API key. This is the only type in the gateway that touches the Google
/// connector package, so a future provider can be added without disturbing the
/// abstraction layer.
/// </summary>
public sealed class GeminiLlmProviderFactory : ILlmProviderFactory
{
    private readonly string _apiKey;
    private readonly string _chatModel;
    private readonly string _embeddingModel;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the
    /// Gemini block and throws <see cref="ArgumentException"/> on bad values.
    /// </summary>
    public GeminiLlmProviderFactory(IOptions<LlmOptions> options)
        : this(options, httpClient: null)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be
    /// stubbed so the HTTP boundary is exercised without reaching the live
    /// Google AI endpoint. When supplied, both SK services are registered
    /// against it.
    /// </summary>
    internal GeminiLlmProviderFactory(IOptions<LlmOptions> options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var gemini = options.Value.Gemini;
        if (gemini is null)
        {
            throw new ArgumentException("LlmOptions.Gemini must be configured.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(gemini.ApiKey))
        {
            throw new ArgumentException(
                "GeminiOptions.ApiKey must be non-empty.", nameof(options));
        }

        var chat = gemini.Chat;
        if (chat is null || string.IsNullOrWhiteSpace(chat.Model))
        {
            throw new ArgumentException(
                "GeminiOptions.Chat.Model must be non-empty.", nameof(options));
        }

        var embeddings = gemini.Embeddings;
        if (embeddings is null)
        {
            throw new ArgumentException(
                "GeminiOptions.Embeddings must be configured.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(embeddings.Model))
        {
            throw new ArgumentException(
                "GeminiOptions.Embeddings.Model must be non-empty.", nameof(options));
        }
        if (embeddings.MaxBatchSize < 1)
        {
            throw new ArgumentException(
                "GeminiOptions.Embeddings.MaxBatchSize must be >= 1.", nameof(options));
        }
        if (embeddings.HttpTimeoutSeconds < 1)
        {
            throw new ArgumentException(
                "GeminiOptions.Embeddings.HttpTimeoutSeconds must be >= 1.", nameof(options));
        }
        if (embeddings.ExpectedDimensions < 0)
        {
            throw new ArgumentException(
                "GeminiOptions.Embeddings.ExpectedDimensions must be >= 0.", nameof(options));
        }

        _apiKey = gemini.ApiKey;
        _chatModel = chat.Model;
        _embeddingModel = embeddings.Model;
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public string Name => "Gemini";

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

    // SKEXP0070: the Google AI connector surface is experimental. The consumed
    // type (IChatCompletionService) is stable, so the suppression is scoped to
    // the registration call only and never leaks to consumers of the gateway.
#pragma warning disable SKEXP0070
    private void AddChat(IKernelBuilder builder)
        => builder.AddGoogleAIGeminiChatCompletion(
            modelId: _chatModel, apiKey: _apiKey, httpClient: _httpClient);
#pragma warning restore SKEXP0070

    // SKEXP0070: the Google AI connector surface is experimental. The consumed
    // type (IEmbeddingGenerator<string, Embedding<float>>) is stable, so the
    // suppression is scoped to the registration call only and never leaks to
    // consumers of the gateway.
#pragma warning disable SKEXP0070
    private void AddEmbeddings(IKernelBuilder builder)
        => builder.AddGoogleAIEmbeddingGenerator(
            modelId: _embeddingModel, apiKey: _apiKey, httpClient: _httpClient);
#pragma warning restore SKEXP0070
}
