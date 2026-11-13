using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp;
using OllamaSharp.Models;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.Ollama;

/// <summary>
/// <see cref="ILlmProviderFactory"/> that wires Semantic Kernel services for Ollama. Chat goes to Ollama's native chat
/// endpoint through the Ollama client's chat client, handed to Semantic Kernel as an
/// <see cref="IChatCompletionService"/> whose function calls run through the kernel, so its filters and the write gate
/// apply. Every chat request asks for the configured context length, so the server never loads the model with its own
/// smaller default and cuts the prompt without saying so. Embeddings stay on the Semantic Kernel Ollama connector. No
/// Ollama client type leaves this folder.
/// </summary>
public sealed class OllamaLlmProviderFactory : ILlmProviderFactory
{
    private readonly Uri _endpoint;
    private readonly string _chatModel;
    private readonly int _contextLength;
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
    /// to the validated endpoint and the chat and embedding services use it.
    /// </summary>
    internal OllamaLlmProviderFactory(IOptions<LlmOptions> options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var ollama = options.Value.Ollama ?? throw new ArgumentException("LlmOptions.Ollama must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(ollama.Endpoint))
        {
            throw new ArgumentException(
                "OllamaOptions.Endpoint must be non-empty.", nameof(options));
        }
        if (!Uri.TryCreate(ollama.Endpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"OllamaOptions.Endpoint '{PlainHttpEndpoint.Shown(ollama.Endpoint)}' is not a valid absolute " +
                "http or https URI.",
                nameof(options));
        }

        var chat = ollama.Chat;
        if (chat is null || string.IsNullOrWhiteSpace(chat.Model))
        {
            throw new ArgumentException(
                "OllamaOptions.Chat.Model must be non-empty.", nameof(options));
        }
        if (chat.ContextLength < 4096)
        {
            throw new ArgumentException(
                "OllamaOptions.Chat.ContextLength must be >= 4096.", nameof(options));
        }

        var embeddings = ollama.Embeddings ?? throw new ArgumentException(
                "OllamaOptions.Embeddings must be configured.", nameof(options));
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
        _contextLength = chat.ContextLength;
        _embeddingModel = embeddings.Model;
        _httpClient = httpClient;
    }

    public string Name => "Ollama";

    public void AddToKernel(IKernelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IChatCompletionService>(_ => CreateChatCompletionService(_chatModel));
        if (_httpClient is not null)
        {
            _httpClient.BaseAddress ??= _endpoint;
            AddEmbeddings(builder, _httpClient);
            return;
        }

        AddEmbeddings(builder, _endpoint);
    }

    /// <inheritdoc/>
    public IChatCompletionService CreateChatCompletionService(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        OllamaApiClient client;
        if (_httpClient is not null)
        {
            _httpClient.BaseAddress ??= _endpoint;
            client = new OllamaApiClient(_httpClient, model);
        }
        else
        {
            client = new OllamaApiClient(_endpoint, model);
        }

        var chat = new ChatClientBuilder(new OllamaChatClient(client))
            .ConfigureOptions(options => options.AddOllamaOption(OllamaOption.NumCtx, _contextLength))
            .UseKernelFunctionInvocation()
            .Build();
        chat.GetService<FunctionInvokingChatClient>()!.IncludeDetailedErrors = true;
        return chat.AsChatCompletionService();
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
