namespace Scout.Conversation.Abstractions;

/// <summary>
/// A recorded voice message attached to an inbound <see cref="ChatMessage"/>.
/// The adapter supplies <paramref name="OpenAsync"/>, which downloads the audio
/// on demand, so no platform type reaches the core and nothing is fetched before
/// the conductor has accepted the sender.
/// </summary>
/// <param name="ContentType">MIME type the platform reported, for example <c>audio/ogg</c>.</param>
/// <param name="SizeBytes">Size the platform reported, in bytes.</param>
/// <param name="Duration">Length the platform reported, when it reports one.</param>
/// <param name="OpenAsync">Opens a read stream over the audio bytes.</param>
public sealed record AudioAttachment(
    string ContentType,
    long SizeBytes,
    TimeSpan? Duration,
    Func<CancellationToken, Task<Stream>> OpenAsync);
