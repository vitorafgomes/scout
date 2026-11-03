namespace Scout.Plugin.YouTube.Abstractions;

/// <summary>
/// Read-only access to the YouTube Data API. Listing only: no upload, edit, comment or
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
}
