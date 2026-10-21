using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Scout.Plugin.CodeReview.Abstractions;

namespace Scout.Plugin.CodeReview;

/// <summary>
/// Model-backed <see cref="ICodeReviewer"/> over the shared
/// <see cref="IChatCompletionService"/>. Builds a two-message chat (system prompt
/// for the kind plus a single untrusted-wrapped user message) and returns the
/// model's content. No function-calling is enabled: a review is a single
/// completion with no tool round-trips. An empty or whitespace response becomes a
/// <see cref="LlmReviewException"/>.
/// </summary>
/// <remarks>
/// The chat service is resolved lazily, not at construction. The shared kernel
/// builds the chat service and also enumerates every registered kernel plugin
/// (this plugin included); pulling the chat service eagerly from this plugin's
/// reviewer would re-enter the kernel under construction and deadlock, so
/// resolution is deferred to the first review.
/// </remarks>
internal sealed class CodeReviewer : ICodeReviewer
{
    private readonly Func<IChatCompletionService> _chatFactory;
    private readonly CodeReviewOptions _options;

    /// <summary>Creates the reviewer over a deferred chat-service factory and options.</summary>
    /// <param name="chatFactory">Resolves the shared chat-completion service on demand. Must be non-null.</param>
    /// <param name="options">The plugin options. Must be non-null.</param>
    public CodeReviewer(Func<IChatCompletionService> chatFactory, IOptions<CodeReviewOptions> options)
    {
        _chatFactory = chatFactory ?? throw new ArgumentNullException(nameof(chatFactory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<string> ReviewAsync(
        ReviewKind kind, CodeReviewInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var history = new ChatHistory();
        history.AddSystemMessage(CodeReviewPromptTemplate.SystemFor(kind));
        history.AddUserMessage(BuildUserMessage(kind, input));

        var settings = BuildSettings();

        // kernel: null - reviews never call tools, so no kernel is supplied.
        var chat = _chatFactory();
        var result = await chat
            .GetChatMessageContentAsync(history, settings, kernel: null, ct)
            .ConfigureAwait(false);

        var content = result.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new LlmReviewException(
                "The model returned an empty review. Try again or rephrase the request.");
        }

        return content;
    }

    private string BuildUserMessage(ReviewKind kind, CodeReviewInput input) => kind switch
    {
        ReviewKind.PullRequest =>
            CodeReviewPromptTemplate.WrapDiff(input.Diff ?? string.Empty),
        ReviewKind.Repository =>
            CodeReviewPromptTemplate.WrapFiles(
                input.Files ?? [],
                _options.CorpusCharBudget,
                input.RepositoryName),
        _ => CodeReviewPromptTemplate.WrapSnippet(WithHints(kind, input)),
    };

    private static string WithHints(ReviewKind kind, CodeReviewInput input)
    {
        var snippet = input.Snippet ?? string.Empty;
        return kind switch
        {
            ReviewKind.Tests when !string.IsNullOrWhiteSpace(input.Framework) =>
                $"Test framework: {input.Framework}\n{snippet}",
            ReviewKind.Explain when !string.IsNullOrWhiteSpace(input.Level) =>
                $"Explanation level: {input.Level}\n{snippet}",
            _ => snippet,
        };
    }

    private OpenAIPromptExecutionSettings BuildSettings()
    {
        var settings = new OpenAIPromptExecutionSettings();

        if (_options.Temperature is { } temperature)
        {
            settings.Temperature = temperature;
        }
        if (_options.MaxTokens is { } maxTokens)
        {
            settings.MaxTokens = maxTokens;
        }

        return settings;
    }
}
