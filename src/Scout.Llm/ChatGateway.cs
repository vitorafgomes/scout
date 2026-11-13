using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm;

/// <summary>
/// Streaming-first <see cref="IChatGateway"/> over the shared
/// <see cref="IChatCompletionService"/>. Translates the provider-agnostic
/// <see cref="ChatRequest"/> into Semantic Kernel primitives and yields content
/// chunks as they arrive. A copy of the composed <see cref="Kernel"/> holding only
/// the functions the request declares (<see cref="KernelScope.Only"/>) is passed
/// into the streaming call so Semantic Kernel can drive function-calling over that
/// set alone: tool round-trips happen internally and only final assistant content
/// is yielded; an empty or missing set offers no function. When a chat
/// <see cref="ChatCompletionAgent"/> is supplied, the stream is routed through it
/// with the same per-turn kernel, so its instructions apply; the yielded contract is
/// unchanged. A call the model makes to a function the turn did not declare never
/// runs and is logged as a Warning naming the function and the turn's skills.
/// </summary>
internal sealed class ChatGateway(
    IChatCompletionService chat,
    Kernel? kernel = null,
    IOptions<LlmOptions>? options = null,
    ILogger<ChatGateway>? logger = null,
    ChatCompletionAgent? agent = null,
    MediaCapabilities? media = null, ILlmUsageSink? usage = null) : IChatGateway
{
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
            "Anthropic" => ("https://api.anthropic.com", llm?.Anthropic?.Chat?.Model),
            _ => ((string?)null, (string?)null),
        };
        return $"provider={provider} endpoint={Providers.PlainHttpEndpoint.Shown(endpoint)} model={model ?? "unknown"}";
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

        foreach (var call in trace.FunctionCalls.Where(call => !KernelScope.IsDeclared(call, request.Functions ?? [])))
        {
            _logger.LogWarning(
                "The model called {Function}, which this turn ({Skills}) does not declare; it was not offered and did not run.",
                call, request.Skills.Count == 0 ? "no skill" : string.Join(", ", request.Skills));
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
                throw Reported(TerminalFailure(failure));
            }

            if (_source == "Anthropic" && NoTextReason(trace.Finish) is { } reason && !TurnAttachments.Pending)
            {
                throw new LlmNoAnswerException(reason);
            }
        }

        Report(sink => sink.Answered(trace.Usage(_source)));
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

    /// <summary>
    /// The sentence for a stream the Claude API ended with no text, by its <c>stop_reason</c>, so the owner is told why
    /// instead of getting an empty reply; null for any other finish, which keeps the empty-stream placeholder. Only the
    /// reason is used, never the provider's text. Applied only when the provider is Anthropic, because another
    /// provider's finish can carry the same word with another meaning.
    /// </summary>
    internal static string? NoTextReason(string? finish) => finish switch
    {
        "max_tokens" => "The answer was cut off by the model's output limit before any text arrived.",
        "model_context_window_exceeded" => "The answer stopped before any text arrived because the conversation and what Scout read for it filled the model's context window.",
        "refusal" => "The model declined to answer this request.",
        "pause_turn" => "The model paused its turn before any text arrived.",
        "end_turn" or "stop_sequence" => "The model ended its turn without writing an answer.",
        "tool_use" => "The model was still asking to call functions when the answer ended, so no text arrived.",
        _ => null,
    };

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

        var scoped = _kernel is null ? null : KernelScope.Only(_kernel, request.Functions ?? []);
        var settings = Providers.ProviderSettings.BuildSettings(request, offerFunctions: scoped?.Plugins.Count > 0);
        var stream = _chat.GetStreamingChatMessageContentsAsync(
            history, settings, kernel: scoped, cancellationToken);

        // Explicit enumerator so the provider failure -> domain translation can sit in a try
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
            catch (Exception ex) when (Providers.ProviderFailures.TranslateProviderFailure(ex, _chat.GetModelId()) is { } translated)
            {
                throw Reported(translated);
            }

            if (!string.IsNullOrEmpty(content))
            {
                yield return content;
            }
        }
    }

    // Routes the response through the Project Expert agent. The agent owns its
    // base instructions; its tools are the request's declared functions, on the
    // per-turn kernel the options carry. A fresh thread per request keeps the gateway
    // stateless. Persona binds to the agent's "{{$persona}}" template via
    // KernelArguments["persona"]; the system prompt rides AdditionalInstructions.
    // When both are empty the agent's default persona applies. Only non-empty
    // content is yielded.
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

        var hasPersona = !string.IsNullOrWhiteSpace(request.Persona);
        var hasSystemPrompt = !string.IsNullOrWhiteSpace(request.SystemPrompt);
        var scoped = KernelScope.Only(_kernel ?? _agent!.Kernel, request.Functions ?? []);
        var arguments = new KernelArguments(Providers.ProviderSettings.BuildSettings(new ChatRequest { UserMessage = request.UserMessage }, offerFunctions: scoped.Plugins.Count > 0));
        if (hasPersona)
        {
            arguments["persona"] = request.Persona;
        }

        var options = new AgentInvokeOptions
        {
            Kernel = scoped,
            KernelArguments = arguments,
            AdditionalInstructions = hasSystemPrompt ? request.SystemPrompt! : string.Empty,
        };

        var stream = _agent!.InvokeStreamingAsync(
            new[] { message }, thread, options, cancellationToken);

        // Explicit enumerator so the provider failure -> domain translation can sit in a try
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
            catch (Exception ex) when (Providers.ProviderFailures.TranslateProviderFailure(ex, _chat.GetModelId()) is { } translated)
            {
                throw Reported(translated);
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

    private readonly string _source = string.IsNullOrWhiteSpace(options?.Value?.Provider) ? "Ollama" : options.Value.Provider;

    private Exception Reported(Exception exception)
    {
        if (exception is LlmQuotaExceededException { Code: "subscription_sharing_usage_limit_exceeded" })
        {
            Report(sink => sink.UsageLimitReached(_source));
        }

        return exception;
    }

    private void Report(Action<ILlmUsageSink> report)
    {
        if (usage is null)
        {
            return;
        }

        try
        {
            report(usage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The usage of an answer could not be reported to the desktop.");
        }
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

        private readonly UsageTally _usage = new();
        private string? _model;

        public void Observe(StreamingChatMessageContent update)
        {
            Updates++;
            _model = string.IsNullOrEmpty(update.ModelId) ? _model : update.ModelId;
            Providers.ProviderUsage.Count(update.Metadata, _usage);
            Finish = Providers.ProviderStreamEnd.FinishOf(update) ?? Finish;
            Failure = Providers.ProviderStreamEnd.FailureOf(update.InnerContent) ?? Failure;
            foreach (var call in update.Items.OfType<StreamingFunctionCallUpdateContent>())
            {
                if (!string.IsNullOrEmpty(call.Name) && !FunctionCalls.Contains(call.Name))
                {
                    FunctionCalls.Add(call.Name);
                }
            }
        }

        public LlmUsage Usage(string source)
        {
            _usage.CommitPending();
            return new LlmUsage(source, _model, _usage.Prompt, _usage.Completion, _usage.Total, _usage.Cost);
        }
    }
}
