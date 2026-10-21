using Scout.Core.Chat;

namespace Scout.Plugin.CloudStatus;

/// <summary>
/// Keyword-based router that forwards messages containing words like
/// "status", "health", "up", "down", or "healthy" to the CloudStatus plugin
/// and returns a formatted <see cref="ChatResponse"/>.
///
/// This is intentionally simple. Swap for an LLM-based router (Semantic
/// Kernel function calling / planner) once local model reliability is
/// good enough.
/// </summary>
public sealed class CloudStatusMessageRouter : IMessageRouter
{
    private static readonly string[] DefaultKeywords =
        ["status", "health", "healthy", "up", "down", "outage"];

    private readonly CloudStatusFunctions _functions;
    private readonly IReadOnlyCollection<string> _keywords;

    public CloudStatusMessageRouter(CloudStatusFunctions functions)
        : this(functions, DefaultKeywords)
    {
    }

    public CloudStatusMessageRouter(
        CloudStatusFunctions functions,
        IReadOnlyCollection<string> keywords)
    {
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        ArgumentNullException.ThrowIfNull(keywords);
        _keywords = keywords;
    }

    public async Task<ChatResponse?> RouteAsync(
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!MatchesKeyword(message.Text))
        {
            return null;
        }

        var reports = await _functions
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        return ChatResponse.Plain(CloudStatusChatFormatter.Format(reports));
    }

    private bool MatchesKeyword(string text)
    {
        // Token match is cheaper than regex and avoids false positives on
        // substrings (e.g. "distatus" would not match "status").
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
