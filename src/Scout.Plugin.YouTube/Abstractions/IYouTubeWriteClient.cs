namespace Scout.Plugin.YouTube.Abstractions;

/// <summary>
/// The YouTube writes the channel operator runs after the owner approved each one: an upload, a thumbnail, a title, a
/// reply to a comment, a comment held for review. Never
/// offered to the model as a function. Needs the desktop sign-in; an API key cannot write.
/// </summary>
public interface IYouTubeWriteClient
{
    /// <summary>
    /// Upload the file at <paramref name="filePath"/> with YouTube's resumable protocol, resuming after a server error, a
    /// dropped connection or a stall and starting over when the upload session is gone, at most <c>5</c> attempts; the new
    /// video's id. The session is kept in <paramref name="session"/>, so a later call with the same instance resumes it
    /// instead of opening a new one.
    /// </summary>
    Task<string> UploadVideoAsync(string filePath, YouTubeUpload upload, UploadSession session, CancellationToken ct);

    /// <summary>Set the thumbnail of <paramref name="videoId"/> to the PNG or JPEG file at <paramref name="filePath"/>.</summary>
    Task SetThumbnailAsync(string videoId, string filePath, CancellationToken ct);

    /// <summary>
    /// Set the title of <paramref name="videoId"/>, sending the rest of its snippet back as YouTube holds it; nothing is sent
    /// when the video already has that title.
    /// </summary>
    Task UpdateTitleAsync(string videoId, string title, CancellationToken ct);

    /// <summary>The upload and privacy status of <paramref name="videoId"/>; null when YouTube has no such video.</summary>
    Task<YouTubeVideoStatus?> GetVideoStatusAsync(string videoId, CancellationToken ct);

    /// <summary>
    /// Post <paramref name="text"/> as the owner's reply to the top-level comment <paramref name="parentId"/>
    /// (<c>comments.insert</c>); the new reply's id. Not idempotent: each call that YouTube takes is one more reply. A
    /// failure carries the status YouTube answered, a success status when YouTube took the reply but its answer could not
    /// be read, and no status only when nothing was sent.
    /// </summary>
    Task<string> ReplyToCommentAsync(string parentId, string text, CancellationToken ct);

    /// <summary>
    /// Hold the comment <paramref name="commentId"/> for the owner's review on YouTube
    /// (<c>comments.setModerationStatus</c> with <c>heldForReview</c>, never rejected and never banning the author); safe
    /// to repeat.
    /// </summary>
    Task HoldCommentAsync(string commentId, CancellationToken ct);
}

/// <summary>
/// The metadata an upload carries. <see cref="PublishAt"/> schedules the video and needs <see cref="PrivacyStatus"/>
/// <c>private</c>; <see cref="DefaultLanguage"/> is a language code such as <c>en</c>, or null to leave it unset.
/// </summary>
public sealed record YouTubeUpload(
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    string CategoryId,
    string? DefaultLanguage,
    string PrivacyStatus,
    DateTimeOffset? PublishAt,
    bool MadeForKids,
    bool SyntheticMedia);

/// <summary>The resumable session of one upload, kept by the caller between calls; null until YouTube opens one.</summary>
public sealed class UploadSession
{
    /// <summary>The session address YouTube gave, on <c>www.googleapis.com</c>.</summary>
    public Uri? Address { get; set; }
}

/// <summary>A video's upload status (<c>uploaded</c>, <c>processed</c>, ...), privacy status and scheduled publish time.</summary>
public sealed record YouTubeVideoStatus(string UploadStatus, string PrivacyStatus, DateTimeOffset? PublishAt);
