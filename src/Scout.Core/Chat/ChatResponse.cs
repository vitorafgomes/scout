namespace Scout.Core.Chat;

/// <summary>
/// Outbound response produced by the Scout core and rendered by a chat adapter.
/// Two modes:
/// <list type="bullet">
/// <item><description><b>Plain</b> — a complete, immutable text payload (the
/// classic case). <see cref="Text"/> is set, <see cref="Stream"/> is null.</description></item>
/// <item><description><b>Streaming</b> — tokens arrive incrementally. <see cref="Stream"/>
/// is set, <see cref="Text"/> is null. The adapter pulls the async sequence.</description></item>
/// </list>
/// In both modes <see cref="Citations"/> is non-null and may be empty.
/// </summary>
public sealed record ChatResponse
{
    private ChatResponse(
        string? text,
        IAsyncEnumerable<string>? stream,
        IReadOnlyList<Citation>? citations)
    {
        Text = text;
        Stream = stream;
        Citations = citations ?? Array.Empty<Citation>();
    }

    /// <summary>
    /// Backwards-compatible constructor — equivalent to <see cref="Plain"/>.
    /// Rejects null/whitespace text.
    /// </summary>
    public ChatResponse(string Text, IReadOnlyList<Citation>? Citations = null)
        : this(ValidateText(Text), stream: null, Citations)
    {
    }

    /// <summary>Complete text payload. Null when the response is streaming.</summary>
    public string? Text { get; }

    /// <summary>Streaming token sequence. Null when the response is plain.</summary>
    public IAsyncEnumerable<string>? Stream { get; }

    /// <summary>
    /// Source citations that back the response. Empty when not applicable.
    /// Never null.
    /// </summary>
    public IReadOnlyList<Citation> Citations { get; }

    public bool IsStreaming => Stream is not null;

    public static ChatResponse Plain(string text, IReadOnlyList<Citation>? citations = null)
        => new(ValidateText(text), stream: null, citations);

    public static ChatResponse Streaming(
        IAsyncEnumerable<string> tokens,
        IReadOnlyList<Citation>? citations = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return new ChatResponse(text: null, stream: tokens, citations);
    }

    private static string ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text must be non-empty.", nameof(text));
        }
        return text;
    }
}
