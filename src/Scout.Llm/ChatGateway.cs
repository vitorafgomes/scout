using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm;

/// <summary>
/// Streaming-first <see cref="IChatGateway"/> over the shared
/// <see cref="IChatCompletionService"/>. Translates the provider-agnostic
/// <see cref="ChatRequest"/> into Semantic Kernel primitives and yields content
/// chunks as they arrive. The composed <see cref="Kernel"/> is passed into the
/// streaming call so Semantic Kernel can drive function-calling: tool round-trips
/// happen internally and only final assistant content is yielded.
/// </summary>
internal sealed class ChatGateway : IChatGateway
{
    private readonly IChatCompletionService _chat;
    private readonly Kernel? _kernel;
    private readonly ILogger<ChatGateway> _logger;
    private readonly string _target;

    public ChatGateway(
        IChatCompletionService chat,
        Kernel? kernel = null,
        IOptions<LlmOptions>? options = null,
        ILogger<ChatGateway>? logger = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _kernel = kernel;
        _logger = logger ?? NullLogger<ChatGateway>.Instance;

        // Human-readable description of what the gateway talks to, for the
        // communication log. Best-effort: options is optional (e.g. in tests).
        var llm = options?.Value;
        var provider = llm?.Provider ?? "unknown";
        var endpoint = llm?.Ollama?.Endpoint ?? "unknown";
        var model = llm?.Ollama?.Chat?.Model ?? "unknown";
        _target = $"provider={provider} endpoint={endpoint} model={model}";
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.UserMessage))
        {
            throw new ArgumentException(
                "ChatRequest.UserMessage must be non-empty.", nameof(request));
        }

        var history = new ChatHistory();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            history.AddSystemMessage(request.SystemPrompt);
        }
        history.AddUserMessage(request.UserMessage);

        var settings = BuildSettings(request);

        _logger.LogInformation(
            "LLM request -> {Target} (inputChars={InputChars}).",
            _target, request.UserMessage.Length);

        var stream = _chat.GetStreamingChatMessageContentsAsync(
            history, settings, kernel: _kernel, cancellationToken);

        var outputChars = 0;
        await foreach (var chunk in stream.ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                outputChars += chunk.Content.Length;
                yield return chunk.Content;
            }
        }

        _logger.LogInformation(
            "LLM response complete <- {Target} (outputChars={OutputChars}).",
            _target, outputChars);
    }

    public async Task<string> CompleteAsync(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        await foreach (var chunk in StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            builder.Append(chunk);
        }
        return builder.ToString();
    }

    // Always enable automatic function-calling so the kernel's plugins are
    // offered to the model and tool round-trips are handled internally.
    // Temperature and max tokens are forwarded only when the request sets them.
    internal static OpenAIPromptExecutionSettings BuildSettings(ChatRequest request)
    {
        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
        };

        if (request.Temperature is { } temperature)
        {
            settings.Temperature = temperature;
        }
        if (request.MaxTokens is { } maxTokens)
        {
            settings.MaxTokens = maxTokens;
        }

        return settings;
    }
}
