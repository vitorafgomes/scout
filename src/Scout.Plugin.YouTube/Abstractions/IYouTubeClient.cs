namespace Scout.Plugin.YouTube.Abstractions;

/// <summary>
/// Read-only access to the YouTube Data API. Listing only: no upload, edit, comment write or
/// playlist operation exists by design.
/// </summary>
public interface IYouTubeClient
{
    /// <summary>
    /// The channel of the signed-in account. Needs the desktop sign-in; an API key cannot
    /// read it. Null when the account has no channel.
    /// </summary>
    Task<YouTubeChannel?> GetMyChannelAsync(CancellationToken ct);

    /// <summary>The public channel with <paramref name="handle"/> (with or without "@"); null when none has it.</summary>
    Task<YouTubeChannel?> GetChannelByHandleAsync(string handle, CancellationToken ct);

    /// <summary>The latest <paramref name="max"/> uploads (at most 50) of an uploads playlist, newest first.</summary>
    Task<IReadOnlyList<YouTubeVideo>> ListUploadsAsync(string uploadsPlaylistId, int max, CancellationToken ct);

    /// <summary>The videos with the given ids (at most 50), in the order asked; unknown ids are absent.</summary>
    Task<IReadOnlyList<YouTubeVideo>> GetVideosAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>
    /// One <c>search.list</c> call for videos matching <paramref name="query"/>, most relevant first, at most
    /// 25 (<paramref name="max"/> is clamped to 1..25). Each call spends one of the 100 searches YouTube allows a day.
    /// </summary>
    Task<IReadOnlyList<YouTubeSearchResult>> SearchVideosAsync(string query, int max, CancellationToken ct);

    /// <summary>
    /// One page of up to 100 comment threads on every video of <paramref name="channelId"/>, newest first, in plain text;
    /// <paramref name="pageToken"/> null for the first page. YouTube leaves out the threads held for review or likely spam.
    /// It needs the <c>youtube.force-ssl</c> scope of the write sign-in; a read-only sign-in is refused with a sentence that
    /// asks the owner to allow writes again.
    /// </summary>
    Task<YouTubeCommentThreadPage> ListCommentThreadsAsync(string channelId, string? pageToken, CancellationToken ct);
}
