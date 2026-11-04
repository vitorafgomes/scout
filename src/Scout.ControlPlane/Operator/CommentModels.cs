namespace Scout.ControlPlane.Operator;

/// <summary>
/// One top-level comment a stranger wrote on the owner's channel. The worker sets its kind, theme, draft and the
/// statuses it may move it to; only the owner sets <c>ApprovedText</c> and <c>DecidedAt</c>. <c>FetchedAt</c> is when
/// the control plane first stored it, and the 30-day clearing keys on it.
/// </summary>
public sealed record AudienceComment(
    string Id,
    string ThreadId,
    string VideoId,
    string VideoTitle,
    string Author,
    string Text,
    DateTimeOffset PublishedAt,
    long LikeCount,
    string Kind,
    string Theme,
    string Status,
    string Draft,
    string? ApprovedText,
    string? ReplyId,
    string? Error,
    DateTimeOffset? DecidedAt,
    DateTimeOffset FetchedAt);

/// <summary>A video idea drawn from a viewer's request, <c>proposed</c> until the owner uses or dismisses it.</summary>
public sealed record AudienceIdea(string Id, string Text, string CommentId, string Status, string? ProjectId);

/// <summary>
/// The comments Scout keeps in <c>comments.json</c>, the ideas drawn from them, when the worker last synced, the
/// owner's pending sync request and the last sync's failure.
/// </summary>
public sealed record CommentsState(
    IReadOnlyList<AudienceComment> Comments,
    IReadOnlyList<AudienceIdea> Ideas,
    DateTimeOffset? SyncedAt,
    DateTimeOffset? SyncRequestedAt,
    string? SyncError)
{
    /// <summary>The state before the first sync.</summary>
    public static CommentsState Empty { get; } = new([], [], null, null, null);
}
