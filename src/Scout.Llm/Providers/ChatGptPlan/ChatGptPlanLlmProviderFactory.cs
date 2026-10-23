using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI.Responses;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.ChatGptPlan;

/// <summary>
/// <see cref="ILlmProviderFactory"/> that answers through the user's ChatGPT plan.
/// Chat goes to the OpenAI Responses API with the access token from the
/// configured token file, through a function-invoking chat client handed to
/// Semantic Kernel as an <see cref="IChatCompletionService"/>. The plan token is
/// accepted only for responses, so this provider has no embeddings: its generator
/// fails every call with <see cref="NotSupportedException"/> and makes no network call.
/// </summary>
public sealed class ChatGptPlanLlmProviderFactory : ILlmProviderFactory
{
    private readonly string _tokenFile;
    private readonly string _chatModel;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the
    /// ChatGptPlan block and throws <see cref="ArgumentException"/> on bad values.
    /// The token file is not read until the first request.
    /// </summary>
    public ChatGptPlanLlmProviderFactory(IOptions<LlmOptions> options)
        : this(options, httpClient: null)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be stubbed
    /// so the Responses API boundary is exercised without reaching a live endpoint.
    /// </summary>
    internal ChatGptPlanLlmProviderFactory(IOptions<LlmOptions> options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var plan = options.Value.ChatGptPlan;
        if (plan is null || string.IsNullOrWhiteSpace(plan.TokenFile))
        {
            throw new ArgumentException(
                "ChatGptPlanOptions.TokenFile must be non-empty.", nameof(options));
        }

        if (plan.Chat is null || string.IsNullOrWhiteSpace(plan.Chat.Model))
        {
            throw new ArgumentException(
                "ChatGptPlanOptions.Chat.Model must be non-empty.", nameof(options));
        }

        _tokenFile = plan.TokenFile;
        _chatModel = plan.Chat.Model;
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public string Name => "ChatGptPlan";

    /// <inheritdoc/>
    public void AddToKernel(IKernelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IChatCompletionService>(_ => CreateChatCompletionService());
        builder.Services.AddSingleton(CreateEmbeddingGenerator());
    }

    /// <summary>
    /// Build the embedding generator. The ChatGPT plan offers no embeddings, so
    /// every call fails at once with <see cref="NotSupportedException"/>.
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator()
        => new NoEmbeddingGenerator();

    // OPENAI001: the Responses API types and their IChatClient adapter are marked
    // experimental in the OpenAI SDK. The consumed type (IChatCompletionService) is
    // stable, so the suppression is scoped to this builder only.
#pragma warning disable OPENAI001
    private IChatCompletionService CreateChatCompletionService()
    {
        var clientOptions = new ResponsesClientOptions { RetryPolicy = new NoRetryOnUsageLimitPolicy() };
        if (_httpClient is not null)
        {
            clientOptions.Transport = new HttpClientPipelineTransport(_httpClient);
        }

        var responses = new ResponsesClient(new ChatGptPlanTokenPolicy(_tokenFile), clientOptions)
            .AsIChatClient(_chatModel);

        return new ChatGptPlanChatClient(responses)
            .AsBuilder()
            .UseFunctionInvocation()
            .Build()
            .AsChatCompletionService();
    }
#pragma warning restore OPENAI001

    private sealed class NoEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                new NotSupportedException("Embeddings are not available with the ChatGPT plan provider."));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class NoRetryOnUsageLimitPolicy() : ClientRetryPolicy(maxRetries: 2)
    {
        protected override bool ShouldRetry(PipelineMessage message, Exception? exception)
            => message.Response?.Status != 429 && base.ShouldRetry(message, exception);
    }
}
