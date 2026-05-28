namespace Scout.Llm.Abstractions;

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
        DateTimeOffset? Timestamp = null)
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
    }

    public string UserId { get; }
    public string ChannelId { get; }
    public string Text { get; }
    public DateTimeOffset Timestamp { get; }
}
