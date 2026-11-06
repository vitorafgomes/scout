namespace Scout.Conversation.Abstractions;

/// <summary>
/// Inbound message received by a chat adapter and handed to the Scout core.
/// Immutable; validated at construction. <see cref="Text"/> may be empty only
/// when the message is a voice message (<see cref="Audio"/> is set).
/// </summary>
public sealed record ChatMessage
{
    public ChatMessage(
        string UserId,
        string ChannelId,
        string Text,
        DateTimeOffset? Timestamp = null,
        AudioAttachment? Audio = null,
        bool IsDirectMessage = false)
    {
        if (string.IsNullOrWhiteSpace(UserId))
        {
            throw new ArgumentException("UserId must be non-empty.", nameof(UserId));
        }
        if (string.IsNullOrWhiteSpace(ChannelId))
        {
            throw new ArgumentException("ChannelId must be non-empty.", nameof(ChannelId));
        }
        if (Audio is null && string.IsNullOrWhiteSpace(Text))
        {
            throw new ArgumentException("Text must be non-empty.", nameof(Text));
        }

        this.UserId = UserId;
        this.ChannelId = ChannelId;
        this.Text = Text;
        this.Timestamp = Timestamp ?? DateTimeOffset.UtcNow;
        this.Audio = Audio;
        this.IsDirectMessage = IsDirectMessage;
    }

    public string UserId { get; }
    public string ChannelId { get; }
    public string Text { get; }
    public DateTimeOffset Timestamp { get; }

    /// <summary>True when the adapter received the message in a one-to-one conversation with the bot.</summary>
    public bool IsDirectMessage { get; }

    /// <summary>The recorded voice message, when this message is one.</summary>
    public AudioAttachment? Audio { get; }

    /// <summary>
    /// True only for a message typed or spoken by the owner on this machine through the local
    /// adapter; it passes the allow-list and is never a pairing attempt. Chat platform adapters never set it.
    /// </summary>
    public bool IsLocalOwner { get; init; }

    /// <summary>
    /// Opaque reference to the conversation this message came from, <c>&lt;adapter&gt;:&lt;payload&gt;</c>,
    /// filled by the adapter so a later notice can be posted there. Never shown to the model or logged.
    /// </summary>
    public string? OriginReference { get; init; }

    /// <summary>A picture the owner attached to this message, sent to the model with its text.</summary>
    public ImageAttachment? Image { get; init; }

#pragma warning disable IDE0051 // the record's synthesized ToString calls PrintMembers; the analyzer does not see that call
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("UserId = ").Append(UserId)
            .Append(", ChannelId = ").Append(ChannelId)
            .Append(", Text = ").Append(Text)
            .Append(", Timestamp = ").Append(Timestamp)
            .Append(", IsDirectMessage = ").Append(IsDirectMessage)
            .Append(", Audio = ").Append(Audio)
            .Append(", IsLocalOwner = ").Append(IsLocalOwner)
            .Append(", Image = ").Append(Image is { } image ? $"{image.Bytes.Length} bytes" : "null");
        return true;
    }
#pragma warning restore IDE0051
}
