namespace Scout.Conversation.Abstractions;

/// <summary>
/// Outbound response produced by the conversation core. Streaming-first: the
/// body is always an async token sequence. A complete string is modelled as a
/// one-element sequence via <see cref="FromText"/> (forced non-streaming), so
/// the stream-drive loop has a single code path regardless of the responder.
/// <see cref="Citations"/> is non-null and may be empty.
/// </summary>
public sealed record ChatResponse
{
    public ChatResponse(
        IAsyncEnumerable<string> Tokens,
        IReadOnlyList<Citation>? Citations = null)
    {
        ArgumentNullException.ThrowIfNull(Tokens);

        this.Tokens = Tokens;
        this.Citations = Citations ?? Array.Empty<Citation>();
    }

    /// <summary>Token sequence yielding response content as it arrives.</summary>
    public IAsyncEnumerable<string> Tokens { get; }

    /// <summary>
    /// Source citations that back the response. Empty when not applicable.
    /// Never null.
    /// </summary>
    public IReadOnlyList<Citation> Citations { get; }

    /// <summary>
    /// Wrap a complete string as a single-element token sequence. For the rare
    /// responder that produces the whole text at once.
    /// </summary>
    public static ChatResponse FromText(
        string text,
        IReadOnlyList<Citation>? citations = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text must be non-empty.", nameof(text));
        }

        return new ChatResponse(Single(text), citations);
    }

    private static async IAsyncEnumerable<string> Single(string text)
    {
        yield return text;
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
