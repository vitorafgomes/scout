namespace Scout.Conversation.Abstractions;

/// <summary>
/// Inbound message received by a chat adapter and handed to the Scout core.
/// Immutable; validated at construction.
/// </summary>
public sealed record ChatMessage
{
    public ChatMessage(
        string UserId,
        string ChannelId,
        string Text,
        DateTimeOffset? Timestamp = null,
        bool IsDirectMessage = false,
        string? AdapterName = null)
    {
        if (string.IsNullOrWhiteSpace(UserId))
        {
            throw new ArgumentException("UserId must be non-empty.", nameof(UserId));
        }
        if (string.IsNullOrWhiteSpace(ChannelId))
        {
            throw new ArgumentException("ChannelId must be non-empty.", nameof(ChannelId));
        }
        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new ArgumentException("Text must be non-empty.", nameof(Text));
        }

        this.UserId = UserId;
        this.ChannelId = ChannelId;
        this.Text = Text;
        this.Timestamp = Timestamp ?? DateTimeOffset.UtcNow;
        this.IsDirectMessage = IsDirectMessage;
        this.AdapterName = AdapterName;
    }

    public string UserId { get; }
    public string ChannelId { get; }
    public string Text { get; }
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// True when the message arrived over a 1:1 direct message rather than a
    /// shared channel/guild/group. Computed by the adapter translator and used to
    /// gate output disclosure, never to authorize a write. Defaults to false.
    /// </summary>
    public bool IsDirectMessage { get; }

    /// <summary>
    /// Name of the chat adapter that produced this message (e.g. "Discord",
    /// "Slack", "Teams"). Null when unknown. Purely descriptive.
    /// </summary>
    public string? AdapterName { get; }
}
