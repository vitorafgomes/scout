namespace Scout.Conversation.Abstractions;

/// <summary>
/// A picture attached to an inbound <see cref="ChatMessage"/>, as encoded bytes and their MIME type.
/// It lives only as long as the message and is never logged or stored.
/// </summary>
/// <param name="ContentType">The MIME type of <paramref name="Bytes"/>, for example <c>image/jpeg</c>.</param>
/// <param name="Bytes">The encoded image.</param>
public sealed record ImageAttachment(string ContentType, ReadOnlyMemory<byte> Bytes);
