namespace Scout.Plugin.YouTube.Abstractions;

/// <summary>
/// One comment thread on the channel, as its top-level comment describes it: the thread and comment ids, the video it is
/// on, the author's display name and channel id (null when YouTube gives none), the comment's plain text, when it was
/// published and its likes.
/// </summary>
public sealed record YouTubeCommentThread(
    string ThreadId,
    string CommentId,
    string VideoId,
    string AuthorDisplayName,
    string? AuthorChannelId,
    string Text,
    DateTimeOffset PublishedAt,
    long LikeCount);

/// <summary>One page of comment threads, newest first, and the token of the next page; null on the last page.</summary>
public sealed record YouTubeCommentThreadPage(IReadOnlyList<YouTubeCommentThread> Threads, string? NextPageToken);
