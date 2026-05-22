using Scout.Core.Chat;
using Scout.Plugin.CodeQA.Functions;

namespace Scout.Plugin.CodeQA.Routing;

/// <summary>
/// Keyword-based router that forwards code-related chat messages to the
/// CodeQA plugin and returns the composed answer with citations.
///
/// The match rule is intentionally dumb: whole-token, case-insensitive,
/// punctuation-split. LLM-driven routing is a follow-up once local model
/// reliability is good enough.
/// </summary>
public sealed class CodeQAMessageRouter : IMessageRouter
{
    private static readonly string[] DefaultKeywords =
        ["code", "how", "where", "find", "explain"];

    private readonly CodeQAFunctions _functions;
    private readonly IReadOnlyCollection<string> _keywords;

    /// <summary>Creates a router with the default keyword set.</summary>
    public CodeQAMessageRouter(CodeQAFunctions functions)
        : this(functions, keywords: null)
    {
    }

    /// <summary>
    /// Creates a router with a caller-supplied keyword set. A null set
    /// falls back to the defaults.
    /// </summary>
    public CodeQAMessageRouter(
        CodeQAFunctions functions,
        IReadOnlyCollection<string>? keywords)
    {
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        _keywords = keywords ?? DefaultKeywords;
    }

    /// <inheritdoc />
    public async Task<ChatResponse?> RouteAsync(
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!MatchesKeyword(message.Text))
        {
            return null;
        }

        var streamingAnswer = await _functions
            .StartStreamingAskAsync(message.Text, cancellationToken)
            .ConfigureAwait(false);

        return ChatResponse.Streaming(streamingAnswer.Tokens, streamingAnswer.Citations);
    }

    private bool MatchesKeyword(string text)
    {
        // Token match is cheaper than regex and avoids false positives on
        // substrings (e.g. "however" would not match "how").
        var tokens = text.Split(
            new[] { ' ', '\t', '\r', '\n', ',', '.', '!', '?', ':' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            foreach (var keyword in _keywords)
            {
                if (string.Equals(token, keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
