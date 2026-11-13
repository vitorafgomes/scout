using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.Anthropic;

/// <summary>
/// <see cref="ILlmProviderFactory"/> that answers through the Claude API with the owner's Anthropic API key. The
/// official Anthropic client's chat client is handed to Semantic Kernel as an <see cref="IChatCompletionService"/>
/// whose function calls run through the kernel, so its filters and the write gate apply. Anthropic offers no
/// embeddings: the generator fails every call with <see cref="NotSupportedException"/> and makes no network call.
/// No Anthropic type leaves this folder.
/// </summary>
public sealed class AnthropicLlmProviderFactory : ILlmProviderFactory
{
    private const string BaseUrl = "https://api.anthropic.com";

    private readonly string _apiKey;
    private readonly string _chatModel;
    private readonly int _maxOutputTokens;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the Anthropic block and throws
    /// <see cref="ArgumentException"/> on bad values.
    /// </summary>
    public AnthropicLlmProviderFactory(IOptions<LlmOptions> options)
        : this(options, httpClient: null)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be stubbed so the Claude API boundary is
    /// exercised without reaching the live endpoint.
    /// </summary>
    internal AnthropicLlmProviderFactory(IOptions<LlmOptions> options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var anthropic = options.Value.Anthropic;
        if (anthropic is null || string.IsNullOrWhiteSpace(anthropic.ApiKey))
        {
            throw new ArgumentException("AnthropicOptions.ApiKey must be non-empty.", nameof(options));
        }

        if (anthropic.Chat is null || string.IsNullOrWhiteSpace(anthropic.Chat.Model))
        {
            throw new ArgumentException("AnthropicOptions.Chat.Model must be non-empty.", nameof(options));
        }

        if (anthropic.Chat.MaxOutputTokens <= 0)
        {
            throw new ArgumentException("AnthropicOptions.Chat.MaxOutputTokens must be greater than zero.", nameof(options));
        }

        _apiKey = anthropic.ApiKey;
        _chatModel = anthropic.Chat.Model;
        _maxOutputTokens = anthropic.Chat.MaxOutputTokens;
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public string Name => "Anthropic";

    /// <inheritdoc/>
    public void AddToKernel(IKernelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IChatCompletionService>(_ => CreateChatCompletionService(_chatModel));
        builder.Services.AddSingleton(CreateEmbeddingGenerator());
    }

    /// <summary>
    /// Build the embedding generator. Anthropic offers no embeddings, so every call fails at once with
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator()
        => new NoEmbeddingGenerator();

    /// <inheritdoc/>
    public IChatCompletionService CreateChatCompletionService(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var client = _httpClient is null
            ? new AnthropicClient { ApiKey = _apiKey, BaseUrl = BaseUrl }
            : new AnthropicClient { ApiKey = _apiKey, BaseUrl = BaseUrl, HttpClient = _httpClient };

        return new AnthropicChatClient(client.AsIChatClient(model, _maxOutputTokens), _apiKey)
            .AsBuilder()
            .UseKernelFunctionInvocation()
            .Build()
            .AsChatCompletionService();
    }

    private sealed class NoEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                new NotSupportedException("Embeddings are not available with the Anthropic provider."));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
