using System.Text;
using Discord;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord.Streaming;

/// <summary>
/// <see cref="IResponseSink"/> backed by a single Discord embed in the
/// originating channel. Begin posts an embed seeded with the cursor; Update
/// replaces the embed description with the cumulative text plus cursor;
/// Complete replaces it without the cursor and appends a citation footer; Fail
/// appends an error footer to the partial text. The transport edit is behind a
/// delegate seam so the sink is unit-testable without Discord.Net network.
/// </summary>
public sealed class DiscordResponseSink : IResponseSink
{
    internal const int MaxEmbedDescriptionLength = 4096;
    internal const string CursorGlyph = "▌";
    internal const string ConnectionErrorFooter = "\n\n(connection error)";

    private readonly Func<string, CancellationToken, Task> _postInitial;
    private readonly Func<string, CancellationToken, Task> _edit;
    private readonly ILogger _logger;

    private bool _begun;
    private bool _terminal;

    /// <summary>
    /// Production constructor. Posts and edits a Discord embed in
    /// <paramref name="channel"/>.
    /// </summary>
    public DiscordResponseSink(IMessageChannel channel, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _logger = logger ?? NullLogger.Instance;

        IUserMessage? posted = null;

        _postInitial = async (body, _) =>
        {
            var embed = new EmbedBuilder().WithDescription(body).Build();
            posted = await channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
        };

        _edit = async (body, _) =>
        {
            if (posted is null)
            {
                return;
            }
            await posted.ModifyAsync(props =>
            {
                props.Embed = new EmbedBuilder().WithDescription(body).Build();
            }).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Test seam. <paramref name="postInitial"/> simulates the initial post and
    /// <paramref name="edit"/> simulates each subsequent embed edit.
    /// </summary>
    internal DiscordResponseSink(
        Func<string, CancellationToken, Task> postInitial,
        Func<string, CancellationToken, Task> edit,
        ILogger? logger = null)
    {
        _postInitial = postInitial ?? throw new ArgumentNullException(nameof(postInitial));
        _edit = edit ?? throw new ArgumentNullException(nameof(edit));
        _logger = logger ?? NullLogger.Instance;
    }

    public RenderCapabilities Capabilities { get; } = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = MaxEmbedDescriptionLength,
        SupportsInterimCitations = false,
    };

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_begun)
        {
            throw new InvalidOperationException("BeginAsync called more than once.");
        }
        _begun = true;

        // Discord rejects empty embed descriptions; seed with the cursor.
        await _postInitial(CursorGlyph, cancellationToken).ConfigureAwait(false);
    }

    public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cumulativeText);
        EnsureActive();

        var body = WithCursor(cumulativeText);
        return _edit(body, cancellationToken);
    }

    public async Task CompleteAsync(
        string finalText,
        IReadOnlyList<Citation> citations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finalText);
        ArgumentNullException.ThrowIfNull(citations);
        EnterTerminal();

        var body = finalText;
        if (citations.Count > 0)
        {
            body += BuildCitationFooter(citations);
        }

        await _edit(Truncate(body), cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(
        string finalTextSoFar,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finalTextSoFar);
        EnterTerminal();

        _logger.LogWarning("Discord response failed: {Reason}", reason);

        var body = finalTextSoFar.Length == 0
            ? ConnectionErrorFooter.TrimStart()
            : finalTextSoFar + ConnectionErrorFooter;

        await _edit(Truncate(body), cancellationToken).ConfigureAwait(false);
    }

    private void EnsureActive()
    {
        if (!_begun)
        {
            throw new InvalidOperationException("UpdateAsync before BeginAsync.");
        }
        if (_terminal)
        {
            throw new InvalidOperationException("UpdateAsync after terminal call.");
        }
    }

    private void EnterTerminal()
    {
        if (_terminal)
        {
            throw new InvalidOperationException("Second terminal call.");
        }
        _terminal = true;
    }

    private static string WithCursor(string body)
    {
        var budget = MaxEmbedDescriptionLength - CursorGlyph.Length;
        return TruncateTo(body, budget) + CursorGlyph;
    }

    private static string Truncate(string body) => TruncateTo(body, MaxEmbedDescriptionLength);

    private static string TruncateTo(string body, int budget)
        => body.Length <= budget ? body : body[^budget..];

    internal static string BuildCitationFooter(IReadOnlyList<Citation> citations)
    {
        var nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append(nl);
        sb.Append(nl);
        sb.Append("Sources:").Append(nl);
        foreach (var citation in citations)
        {
            sb.Append("- ").Append(citation.ToString()).Append(nl);
        }
        return sb.ToString().TrimEnd();
    }
}
