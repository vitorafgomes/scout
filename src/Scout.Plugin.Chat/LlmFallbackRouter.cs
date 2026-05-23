using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Core.Chat;

namespace Scout.Plugin.Chat;

/// <summary>
/// Last-resort router that streams any non-empty inbound message through
/// the LLM chat completion service. Registered after every other
/// <see cref="IMessageRouter"/> so it only kicks in when nothing else
/// matched. Produces no citations (no retrieval is performed).
/// </summary>
public sealed class LlmFallbackRouter : IMessageRouter
{
    private readonly IChatCompletionService _chat;
    private readonly IOptions<LlmChatOptions> _options;
    private readonly ILogger<LlmFallbackRouter> _logger;

    public LlmFallbackRouter(
        IChatCompletionService chat,
        IOptions<LlmChatOptions> options,
        ILogger<LlmFallbackRouter>? logger = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<LlmFallbackRouter>.Instance;
    }

    /// <inheritdoc />
    public Task<ChatResponse?> RouteAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return Task.FromResult<ChatResponse?>(null);
        }

        _logger.LogInformation(
            "llm-fallback engaged: textLen={TextLen}",
            message.Text.Length);

        var stream = StreamAsync(message.Text, cancellationToken);
        return Task.FromResult<ChatResponse?>(ChatResponse.Streaming(stream));
    }

    private async IAsyncEnumerable<string> StreamAsync(
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var opts = _options.Value;

        var history = new ChatHistory();
        history.AddSystemMessage(opts.SystemPrompt);
        history.AddUserMessage(userText);

        var settings = new PromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = opts.Temperature,
                ["max_tokens"] = opts.MaxTokens,
            },
        };

        var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var firstChunkLogged = false;
        var chunkCount = 0;

        var stream = _chat.GetStreamingChatMessageContentsAsync(
            history, settings, kernel: null, cancellationToken);

        await foreach (var chunk in stream.ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                chunkCount++;
                if (!firstChunkLogged)
                {
                    firstChunkLogged = true;
                    var firstChunkMs = (long)System.Diagnostics.Stopwatch
                        .GetElapsedTime(startTimestamp).TotalMilliseconds;
                    _logger.LogInformation(
                        "llm-fallback first chunk after {FirstChunkMs}ms",
                        firstChunkMs);
                }
                yield return chunk.Content!;
            }
        }

        var totalMs = (long)System.Diagnostics.Stopwatch
            .GetElapsedTime(startTimestamp).TotalMilliseconds;
        _logger.LogInformation(
            "llm-fallback completed: chunks={ChunkCount} elapsed={TotalMs}ms",
            chunkCount,
            totalMs);
    }
}
