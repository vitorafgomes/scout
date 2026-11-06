using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using OpenAI;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Providers.OpenAI;

/// <summary>
/// <see cref="ILlmProviderFactory"/> that wires Semantic Kernel services for any
/// OpenAI-compatible API. Chat and embeddings each resolve their own endpoint and
/// key, so chat can target one host (e.g. OpenRouter) while embeddings target
/// another (e.g. OpenAI). This is the only type in the gateway that touches the
/// OpenAI connector package, so the abstraction layer stays connector-free.
/// </summary>
public sealed class OpenAILlmProviderFactory : ILlmProviderFactory
{
    private readonly string _chatApiKey;
    private readonly string? _chatEndpoint;
    private readonly string _chatModel;
    private readonly string _embeddingApiKey;
    private readonly string? _embeddingEndpoint;
    private readonly string _embeddingModel;
    private readonly HttpClient? _httpClient;
    private readonly string _speechModel;
    private readonly string _imageModel;
    private readonly bool _media;

    private static readonly string[] ImageQualities = ["low", "medium"];

    /// <summary>
    /// Create the factory from bound <see cref="LlmOptions"/>. Validates the
    /// OpenAI block and throws <see cref="ArgumentException"/> on bad values.
    /// </summary>
    public OpenAILlmProviderFactory(IOptions<LlmOptions> options)
        : this(options, httpClient: null)
    {
    }

    /// <summary>
    /// Test seam. Accepts an <see cref="HttpClient"/> whose handler can be stubbed
    /// so the HTTP boundary is exercised without reaching a live endpoint. When
    /// supplied, both SK services are registered against it.
    /// </summary>
    internal OpenAILlmProviderFactory(IOptions<LlmOptions> options, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException("LlmOptions must be configured.", nameof(options));
        }

        var openAI = options.Value.OpenAI ?? throw new ArgumentException("LlmOptions.OpenAI must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(openAI.ApiKey))
        {
            throw new ArgumentException(
                "OpenAIOptions.ApiKey must be non-empty.", nameof(options));
        }

        var chat = openAI.Chat;
        if (chat is null || string.IsNullOrWhiteSpace(chat.Model))
        {
            throw new ArgumentException(
                "OpenAIOptions.Chat.Model must be non-empty.", nameof(options));
        }

        var embeddings = openAI.Embeddings ?? throw new ArgumentException(
                "OpenAIOptions.Embeddings must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(embeddings.Model))
        {
            throw new ArgumentException(
                "OpenAIOptions.Embeddings.Model must be non-empty.", nameof(options));
        }

        ValidateOptionalEndpoint(openAI.Endpoint, "OpenAIOptions.Endpoint");
        ValidateOptionalEndpoint(embeddings.Endpoint, "OpenAIOptions.Embeddings.Endpoint");

        _chatApiKey = openAI.ApiKey;
        _chatEndpoint = openAI.Endpoint;
        _chatModel = chat.Model;
        _embeddingApiKey = string.IsNullOrWhiteSpace(embeddings.ApiKey)
            ? openAI.ApiKey
            : embeddings.ApiKey;
        _embeddingEndpoint = embeddings.Endpoint ?? openAI.Endpoint;
        _embeddingModel = embeddings.Model;
        _httpClient = httpClient;
        _speechModel = string.IsNullOrWhiteSpace(openAI.SpeechModel) ? "gpt-4o-mini-tts" : openAI.SpeechModel;
        _imageModel = string.IsNullOrWhiteSpace(openAI.ImageModel) ? "gpt-image-2.5-sunburst" : openAI.ImageModel;
        var quality = string.IsNullOrWhiteSpace(openAI.ImageQuality) ? "medium" : openAI.ImageQuality.Trim();
        if (!ImageQualities.Contains(quality, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"OpenAIOptions.ImageQuality '{openAI.ImageQuality}' must be low or medium.", nameof(options));
        }

        _media = IsDefaultEndpoint(_chatEndpoint) || openAI.MediaOnCustomEndpoint;
        Media = _media
            ? new MediaCapabilities(true, true, Name)
            {
                SpeechSettings = OpenAIMediaSettings.Speech(),
                SpeechVoices = OpenAIMediaSettings.Voices,
                SpeechSettingsFor = OpenAIMediaSettings.Speech,
                ImageSettings = OpenAIMediaSettings.Image(_imageModel, quality),
                Vision = true,
            }
            : new MediaCapabilities(false, false, Name) { Vision = true };
    }

    private static bool IsDefaultEndpoint(string? endpoint)
        => endpoint is null
            || string.Equals(new Uri(endpoint, UriKind.Absolute).Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);

    private static void ValidateOptionalEndpoint(string? endpoint, string name)
    {
        if (endpoint is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"{name} '{endpoint}' is not a valid absolute http or https URI.",
                nameof(endpoint));
        }
    }

    /// <inheritdoc/>
    public string Name => "OpenAI";

    /// <inheritdoc/>
    public MediaCapabilities Media { get; }

    /// <inheritdoc/>
    public void AddToKernel(IKernelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        AddChat(builder);
        AddEmbeddings(builder);
        if (_media)
        {
            AddMedia(builder);
        }
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

    private OpenAIClient BuildClient(string apiKey, string? endpoint)
    {
        var clientOptions = new OpenAIClientOptions();
        if (endpoint is not null)
        {
            clientOptions.Endpoint = new Uri(endpoint, UriKind.Absolute);
        }
        if (_httpClient is not null)
        {
            clientOptions.Transport = new HttpClientPipelineTransport(_httpClient);
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
    }

    // SKEXP0010: the OpenAIClient-based registration overloads are experimental.
    // The consumed type (IChatCompletionService) is stable, so the suppression is
    // scoped to the registration call only and never leaks to gateway consumers.
#pragma warning disable SKEXP0010
    private void AddChat(IKernelBuilder builder)
        => builder.AddOpenAIChatCompletion(
            modelId: _chatModel, openAIClient: BuildClient(_chatApiKey, _chatEndpoint));
#pragma warning restore SKEXP0010

    // SKEXP0010: AddOpenAIEmbeddingGenerator is experimental. The consumed type
    // (IEmbeddingGenerator<string, Embedding<float>>) is stable, so the
    // suppression is scoped to the registration call only.
#pragma warning disable SKEXP0010
    private void AddEmbeddings(IKernelBuilder builder)
        => builder.AddOpenAIEmbeddingGenerator(
            modelId: _embeddingModel,
            openAIClient: BuildClient(_embeddingApiKey, _embeddingEndpoint));
#pragma warning restore SKEXP0010

#pragma warning disable SKEXP0010
    private void AddMedia(IKernelBuilder builder)
    {
        var http = _httpClient ?? new HttpClient
        {
            BaseAddress = _chatEndpoint is null ? null : new Uri(_chatEndpoint, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(3),
        };
        builder.AddOpenAITextToAudio(modelId: _speechModel, apiKey: _chatApiKey, httpClient: http);
        builder.AddOpenAITextToImage(apiKey: _chatApiKey, modelId: _imageModel, httpClient: http);
    }
#pragma warning restore SKEXP0010
}
