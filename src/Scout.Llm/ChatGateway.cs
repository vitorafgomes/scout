using System.ClientModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
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
/// happen internally and only final assistant content is yielded. When a chat
/// <see cref="ChatCompletionAgent"/> is supplied, the stream is routed through it
/// so its instructions and tools apply; the yielded contract is unchanged.
/// </summary>
internal sealed class ChatGateway(
    IChatCompletionService chat,
    Kernel? kernel = null,
    IOptions<LlmOptions>? options = null,
    ILogger<ChatGateway>? logger = null,
    ChatCompletionAgent? agent = null,
    MediaCapabilities? media = null) : IChatGateway
{
    private static readonly HashSet<string> QuotaErrorCodes = new(StringComparer.Ordinal)
    {
        "credit_balance_exhausted",
        "organization_spend_limit_exceeded",
        "project_spend_limit_exceeded",
        "organization_usage_limit_exceeded",
        "subscription_sharing_usage_limit_exceeded",
    };

    private readonly IChatCompletionService _chat = chat ?? throw new ArgumentNullException(nameof(chat));
    private readonly Kernel? _kernel = kernel;
    private readonly ChatCompletionAgent? _agent = agent;
    private readonly ILogger<ChatGateway> _logger = logger ?? NullLogger<ChatGateway>.Instance;
    private readonly string _target = DescribeTarget(options?.Value);
    private readonly bool _vision = media?.Vision ?? false;

    private static string DescribeTarget(LlmOptions? llm)
    {
        var provider = llm?.Provider ?? "unknown";
        var (endpoint, model) = provider switch
        {
            "Ollama" => (llm?.Ollama?.Endpoint, llm?.Ollama?.Chat?.Model),
            "OpenAI" => (llm?.OpenAI?.Endpoint ?? "https://api.openai.com/v1", llm?.OpenAI?.Chat?.Model),
            "Gemini" => ("https://generativelanguage.googleapis.com", llm?.Gemini?.Chat?.Model),
            "ChatGptPlan" => ("https://api.openai.com/v1/responses", llm?.ChatGptPlan?.Chat?.Model),
            _ => ((string?)null, (string?)null),
        };
        return $"provider={provider} endpoint={endpoint ?? "unknown"} model={model ?? "unknown"}";
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

        if (request.Image is not null && !_vision)
        {
            throw new LlmVisionUnavailableException();
        }

        _logger.LogInformation(
            "LLM request -> {Target} (inputChars={InputChars}, image={Image}).",
            _target, request.UserMessage.Length, request.Image is null ? "false" : "true");

        var outputChars = 0;
        var trace = new StreamTrace();
        if (_agent is not null)
        {
            await foreach (var content in StreamAgentAsync(request, trace, cancellationToken)
                .ConfigureAwait(false))
            {
                outputChars += content.Length;
                yield return content;
            }
        }
        else
        {
            await foreach (var content in StreamChatAsync(request, trace, cancellationToken)
                .ConfigureAwait(false))
            {
                outputChars += content.Length;
                yield return content;
            }
        }

        _logger.LogInformation(
            "LLM response complete <- {Target} (outputChars={OutputChars}).",
            _target, outputChars);
        if (outputChars == 0)
        {
            _logger.LogWarning(
                "LLM response empty <- {Target} (updates={Updates}, finish={Finish}, functionCalls={FunctionCalls}).",
                _target, trace.Updates, trace.Finish ?? "unknown", trace.FunctionCalls.Count == 0 ? "none" : string.Join(",", trace.FunctionCalls));
            if (trace.Failure is { } failure)
            {
                throw TerminalFailure(failure);
            }
        }
    }

    /// <summary>
    /// The exception for a stream the provider ended with a failure code and no text: a usage limit or quota is
    /// <see cref="LlmQuotaExceededException"/>, a rate limit is <see cref="LlmRateLimitException"/>, and any other code
    /// is an error naming the code. Only the code is used, never the provider's message.
    /// </summary>
    internal static Exception TerminalFailure(string code)
    {
        if (code.Contains("usage_limit", StringComparison.Ordinal) || code.Contains("quota", StringComparison.Ordinal))
        {
            return new LlmQuotaExceededException(code);
        }

        return code.Contains("rate_limit", StringComparison.Ordinal)
            ? new LlmRateLimitException($"LLM rate limit ({code}).")
            : new InvalidOperationException($"The AI provider ended the answer with an error ({code}).");
    }

    private async IAsyncEnumerable<string> StreamChatAsync(
        ChatRequest request,
        StreamTrace trace,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var history = new ChatHistory();

        // Single system message = persona then system prompt, non-empty parts joined
        // by a blank line. With Persona null this is just SystemPrompt as before.
        var system = string.Join(
            "\n\n",
            new[] { request.Persona, request.SystemPrompt }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        if (!string.IsNullOrWhiteSpace(system))
        {
            history.AddSystemMessage(system);
        }

        // Replay prior turns oldest-first between the system message and the current
        // user message. Empty History keeps the single-turn shape unchanged.
        foreach (var turn in request.History)
        {
            if (turn.Role == ConversationRole.Assistant)
            {
                history.AddAssistantMessage(turn.Text);
            }
            else
            {
                history.AddUserMessage(turn.Text);
            }
        }

        history.Add(UserMessage(request));

        var settings = BuildSettings(request);
        var stream = _chat.GetStreamingChatMessageContentsAsync(
            history, settings, kernel: _kernel, cancellationToken);

        // Explicit enumerator so the 429 -> domain translation can sit in a try
        // while the yield stays outside it (yield is illegal inside try/catch).
        await using var e = stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            string? content;
            try
            {
                if (!await e.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                trace.Observe(e.Current);
                content = e.Current.Content;
            }
            catch (Exception ex) when (TranslateTooManyRequests(ex) is { } translated)
            {
                throw translated;
            }

            if (!string.IsNullOrEmpty(content))
            {
                yield return content;
            }
        }
    }

    // Routes the response through the Project Expert agent. The agent owns its
    // base instructions and tools; a fresh thread per request keeps the gateway
    // stateless. Persona binds to the agent's "{{$persona}}" template via
    // KernelArguments["persona"]; the system prompt rides AdditionalInstructions.
    // When both are empty, options stays null and the agent's default persona
    // applies. Only non-empty content is yielded.
    private async IAsyncEnumerable<string> StreamAgentAsync(
        ChatRequest request,
        StreamTrace trace,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var message = UserMessage(request);

        // Seed the thread with prior turns oldest-first. The current user message is the
        // invoke message, so it is NOT added to the seed (duplicate guard). Empty History
        // yields an empty seed, identical to the prior new() thread.
        var seed = new ChatHistory();
        foreach (var turn in request.History)
        {
            if (turn.Role == ConversationRole.Assistant)
            {
                seed.AddAssistantMessage(turn.Text);
            }
            else
            {
                seed.AddUserMessage(turn.Text);
            }
        }

        ChatHistoryAgentThread thread = new(seed);

        AgentInvokeOptions? options = null;
        var hasPersona = !string.IsNullOrWhiteSpace(request.Persona);
        var hasSystemPrompt = !string.IsNullOrWhiteSpace(request.SystemPrompt);
        if (hasPersona || hasSystemPrompt)
        {
            // AgentInvokeOptions members are init-only. Persona binds via KernelArguments
            // only when present; the system prompt rides AdditionalInstructions only when
            // present (its empty default adds no instruction otherwise).
            options = new AgentInvokeOptions
            {
                KernelArguments = hasPersona
                    ? new KernelArguments { ["persona"] = request.Persona }
                    : null,
                AdditionalInstructions = hasSystemPrompt ? request.SystemPrompt! : string.Empty,
            };
        }

        var stream = _agent!.InvokeStreamingAsync(
            new[] { message }, thread, options, cancellationToken);

        // Explicit enumerator so the 429 -> domain translation can sit in a try
        // while the yield stays outside it (yield is illegal inside try/catch).
        await using var e = stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            string? content;
            try
            {
                if (!await e.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                trace.Observe(e.Current.Message);
                content = e.Current.Message.Content;
            }
            catch (Exception ex) when (TranslateTooManyRequests(ex) is { } translated)
            {
                throw translated;
            }

            if (!string.IsNullOrEmpty(content))
            {
                yield return content;
            }
        }
    }

    private static ChatMessageContent UserMessage(ChatRequest request)
        => request.Image is { } image
            ? new ChatMessageContent(
                AuthorRole.User,
                [new TextContent(request.UserMessage), new ImageContent(image.Bytes, image.ContentType)])
            : new ChatMessageContent(AuthorRole.User, request.UserMessage);

    // Maps a provider HTTP 429 to a domain exception so no SDK exception type
    // leaves this project. The streaming OpenAI connector raises the SDK's
    // ClientResultException while enumerating; other connectors raise
    // HttpOperationException. Returns null for anything that is not a 429, so the
    // original exception propagates unchanged. Must never throw: it runs inside
    // an exception filter, where a throw would be swallowed.
    internal static Exception? TranslateTooManyRequests(Exception ex)
    {
        var status = ex switch
        {
            ClientResultException client => client.Status,
            HttpOperationException http => (int?)http.StatusCode,
            _ => null,
        };
        if (status != (int)HttpStatusCode.TooManyRequests)
        {
            return null;
        }

        return QuotaErrorCode(ex) is { } code
            ? new LlmQuotaExceededException(code, ex)
            : new LlmRateLimitException("LLM rate limit (HTTP 429).", ex);
    }

    // Reads the OpenAI-shaped error body {"error":{"type":...,"code":...}} and
    // returns the provider code when the 429 is a quota or billing refusal. An
    // unreadable or unexpected body means throttling.
    private static string? QuotaErrorCode(Exception ex)
    {
        try
        {
            var body = ex switch
            {
                ClientResultException client => client.GetRawResponse()?.Content?.ToString(),
                HttpOperationException http => http.ResponseContent,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var code = StringProperty(error, "code");
            if (code is not null && QuotaErrorCodes.Contains(code))
            {
                return code;
            }

            return StringProperty(error, "type") == "insufficient_quota"
                ? code ?? "insufficient_quota"
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? StringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    /// <summary>
    /// What a stream reported besides its text: how many updates arrived, the last finish reason or status a
    /// provider gave, and the names of the functions the model called. Never the text or the arguments.
    /// </summary>
    private sealed class StreamTrace
    {
        public int Updates { get; private set; }

        public string? Finish { get; private set; }

        public List<string> FunctionCalls { get; } = [];

        public string? Failure { get; private set; }

        public void Observe(StreamingChatMessageContent update)
        {
            Updates++;
            Finish = FinishOf(update) ?? Finish;
            Failure = FailureOf(update.InnerContent) ?? Failure;
            foreach (var call in update.Items.OfType<StreamingFunctionCallUpdateContent>())
            {
                if (!string.IsNullOrEmpty(call.Name) && !FunctionCalls.Contains(call.Name))
                {
                    FunctionCalls.Add(call.Name);
                }
            }
        }

#pragma warning disable OPENAI001
        private static string? FinishOf(StreamingChatMessageContent update) => update.InnerContent switch
        {
            OpenAI.Responses.StreamingResponseIncompleteUpdate incomplete => $"incomplete ({incomplete.Response?.IncompleteStatusDetails?.Reason})",
            OpenAI.Responses.StreamingResponseFailedUpdate failed => $"failed ({failed.Response?.Error?.Code})",
            OpenAI.Responses.StreamingResponseErrorUpdate error => $"error ({error.Code})",
            OpenAI.Responses.StreamingResponseRefusalDoneUpdate => "refusal",
            OpenAI.Responses.StreamingResponseCompletedUpdate completed => completed.Response?.Status?.ToString(),
            _ => update.Metadata is { } metadata && metadata.TryGetValue("FinishReason", out var reason) ? reason?.ToString() : null,
        };

        private static string? FailureOf(object? inner) => inner switch
        {
            OpenAI.Responses.StreamingResponseFailedUpdate failed => failed.Response?.Error?.Code.ToString() ?? "unknown",
            OpenAI.Responses.StreamingResponseErrorUpdate error => string.IsNullOrEmpty(error.Code) ? "unknown" : error.Code,
            _ => null,
        };
#pragma warning restore OPENAI001
    }
}
