namespace Scout.Conversation.Abstractions;

/// <summary>
/// One chat adapter's side of <see cref="IOriginNotifier"/>: posts into a conversation from the payload of a
/// reference that adapter wrote. Each adapter registers its own in its service collection extension.
/// </summary>
public interface IOriginPoster
{
    /// <summary>The reference prefix this poster resolves, e.g. <c>discord</c>.</summary>
    string Kind { get; }

    /// <summary>
    /// Posts <paramref name="text"/> into the conversation <paramref name="payload"/> names, capped to the
    /// platform limit. Returns false and logs a Warning, without the payload, when the conversation cannot be
    /// resolved or the platform refuses the post.
    /// </summary>
    Task<bool> PostAsync(string payload, string text, CancellationToken ct);
}
