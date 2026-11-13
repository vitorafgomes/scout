namespace Scout.Conversation.Abstractions;

/// <summary>
/// Posts a later message into the conversation an inbound message came from, named by its
/// <see cref="ChatMessage.OriginReference"/>. A descriptive write to the originating channel only.
/// </summary>
public interface IOriginNotifier
{
    /// <summary>
    /// Posts <paramref name="text"/> as one plain-text message, capped to the platform limit, into the
    /// conversation <paramref name="originReference"/> names. Returns false, without throwing, when no running
    /// adapter can resolve the reference or the platform refuses the post.
    /// </summary>
    Task<bool> PostAsync(string originReference, string text, CancellationToken ct);
}
