using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Core.Chat;
using Scout.Core.Hosting;

namespace Scout.Plugin.Agent;

/// <summary>
/// Generic agentic router. Calls the LLM with
/// <see cref="FunctionChoiceBehavior.Auto"/> and the host's resolved kernel
/// (populated with every enabled feature plugin), letting the model invoke
/// functions on its own (auto-invoke) to satisfy the request.
///
/// Registered BEFORE <c>LlmFallbackRouter</c> so that, whenever any feature
/// exposes tools, intent-free questions like "list my repositories" reach a
/// tool-aware completion instead of the read-only fallback (which runs with
/// <c>kernel: null</c> and therefore answers without tools).
///
/// V1 is non-streaming: it returns a complete <see cref="ChatResponse.Plain"/>.
/// When no feature plugin is loaded the router returns <c>null</c>, deferring
/// to the streaming fallback so behavior is unchanged for a bare install.
///
/// Tool surface: read tools and descriptive-write tools (e.g. create_issue)
/// stay exposed to auto-invoke. This respects Scout's read-only /
/// descriptive-write invariant — the model never gains destructive actions;
/// the exposed function set is the security boundary.
/// </summary>
public sealed class AgentRouter : IMessageRouter
{
    private readonly IServiceProvider _services;
    private readonly IOptions<AgentRouterOptions> _options;
    private readonly ILogger<AgentRouter> _logger;

    // ScoutHost depends on IEnumerable<IMessageRouter>, so taking it as a
    // constructor parameter here would form a DI cycle. Resolve it lazily
    // at call time from the provider instead — the host singleton is fully
    // built by the time any message is routed.
    public AgentRouter(
        IServiceProvider services,
        IOptions<AgentRouterOptions> options,
        ILogger<AgentRouter>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AgentRouter>.Instance;
    }

    /// <inheritdoc />
    public async Task<ChatResponse?> RouteAsync(
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return null;
        }

        var kernel = _services.GetRequiredService<ScoutHost>().ResolvedKernel;

        // Empty-kernel guard: with no callable functions there is nothing
        // for the model to invoke, so defer to the streaming fallback and
        // preserve the bare-install experience.
        if (!HasInvocableFunctions(kernel))
        {
            return null;
        }

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var opts = _options.Value;

        var history = new ChatHistory();
        history.AddSystemMessage(opts.SystemPrompt);
        history.AddUserMessage(message.Text);

        var settings = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = opts.Temperature,
                ["max_tokens"] = opts.MaxTokens,
            },
        };

        _logger.LogInformation(
            "agent-router engaged: textLen={TextLen}, plugins={PluginCount}",
            message.Text.Length,
            kernel.Plugins.Count);

        try
        {
            var contents = await chat
                .GetChatMessageContentsAsync(history, settings, kernel, cancellationToken)
                .ConfigureAwait(false);

            var finalText = contents
                .Where(c => c.Role == AuthorRole.Assistant && !string.IsNullOrWhiteSpace(c.Content))
                .Select(c => c.Content)
                .LastOrDefault();

            if (string.IsNullOrWhiteSpace(finalText))
            {
                // Ollama occasionally returns an empty assistant turn after
                // tool invocation. Emit a coherent message rather than null
                // (the router did engage; falling through would re-run the
                // fallback against the same empty result).
                _logger.LogWarning("agent-router produced an empty final message.");
                return ChatResponse.Plain(
                    "I ran the tools but did not get a final answer back. Please try rephrasing.");
            }

            return ChatResponse.Plain(finalText!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "agent-router failed while calling the model.");
            return ChatResponse.Plain(
                "Sorry, I hit an error while working on that. Please try again.");
        }
    }

    /// <summary>
    /// True when the kernel exposes at least one callable function across
    /// its registered plugins.
    /// </summary>
    private static bool HasInvocableFunctions(Kernel kernel)
    {
        foreach (var plugin in kernel.Plugins)
        {
            if (plugin.FunctionCount > 0)
            {
                return true;
            }
        }
        return false;
    }
}
