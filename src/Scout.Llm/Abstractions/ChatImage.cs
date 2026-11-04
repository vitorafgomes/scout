namespace Scout.Llm.Abstractions;

/// <summary>
/// A picture sent with the current user message, as encoded bytes and their MIME type.
/// It is held only for the request and never logged.
/// </summary>
/// <param name="ContentType">The MIME type of <paramref name="Bytes"/>, for example <c>image/jpeg</c>.</param>
/// <param name="Bytes">The encoded image.</param>
public sealed record ChatImage(string ContentType, ReadOnlyMemory<byte> Bytes);
