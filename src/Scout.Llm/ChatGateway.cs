using System.Runtime.CompilerServices;
using System.Text;
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
internal sealed class ChatGateway : IChatGateway
{
    private readonly IChatCompletionService _chat;
    private readonly Kernel? _kernel;
    private readonly ChatCompletionAgent? _agent;
    private readonly ILogger<ChatGateway> _logger;
    private readonly string _target;

    public ChatGateway(
        IChatCompletionService chat,
        Kernel? kernel = null,
        IOptions<LlmOptions>? options = null,
        ILogger<ChatGateway>? logger = null,
        ChatCompletionAgent? agent = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _kernel = kernel;
        _agent = agent;
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

        _logger.LogInformation(
            "LLM request -> {Target} (inputChars={InputChars}).",
            _target, request.UserMessage.Length);

        var outputChars = 0;
        if (_agent is not null)
        {
            await foreach (var content in StreamAgentAsync(request, cancellationToken)
                .ConfigureAwait(false))
            {
                outputChars += content.Length;
                yield return content;
            }
        }
        else
        {
            await foreach (var content in StreamChatAsync(request, cancellationToken)
                .ConfigureAwait(false))
            {
                outputChars += content.Length;
                yield return content;
            }
        }

        _logger.LogInformation(
            "LLM response complete <- {Target} (outputChars={OutputChars}).",
            _target, outputChars);
    }

    private async IAsyncEnumerable<string> StreamChatAsync(
        ChatRequest request,
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

        history.AddUserMessage(request.UserMessage);

        var settings = BuildSettings(request);
        var stream = _chat.GetStreamingChatMessageContentsAsync(
            history, settings, kernel: _kernel, cancellationToken);

        await foreach (var chunk in stream.ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                yield return chunk.Content;
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var message = new ChatMessageContent(AuthorRole.User, request.UserMessage);

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

        await foreach (var item in stream.ConfigureAwait(false))
        {
            var content = item.Message.Content;
            if (!string.IsNullOrEmpty(content))
            {
                yield return content;
            }
        }
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
