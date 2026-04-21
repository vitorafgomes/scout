namespace Scout.Core.Chat;

/// <summary>
/// Outbound response produced by the Scout core and rendered by a chat adapter.
/// Immutable; validated at construction. Mirrors <see cref="ChatMessage"/>'s
/// contract on the outbound side.
/// </summary>
public sealed record ChatResponse
{
    public ChatResponse(string Text, IReadOnlyList<string>? Citations = null)
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new ArgumentException("Text must be non-empty.", nameof(Text));
        }

        this.Text = Text;
        this.Citations = Citations ?? Array.Empty<string>();
    }

    public string Text { get; }

    /// <summary>
    /// Source citations that back the response. Empty when not applicable.
    /// Never null.
    /// </summary>
    public IReadOnlyList<string> Citations { get; }

    public static ChatResponse Plain(string text) => new(text);
}
