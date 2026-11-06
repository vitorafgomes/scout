using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Plugin.YouTube;

/// <summary>
/// Read-only YouTube plugin exposed to the LLM as Semantic Kernel functions. Reading only: no
/// upload, edit, comment or playlist operation exists by design. Every call returns a
/// non-empty, user-safe string; transport and API failures are caught and surfaced as a clean
/// error line so a tool call never throws back into the kernel.
/// </summary>
/// <remarks>Creates the plugin over a read-only YouTube client.</remarks>
/// <param name="client">The YouTube client. Must be non-null.</param>
public sealed partial class YouTubePlugin(IYouTubeClient client)
{
    private const string HandleDescription =
        "Channel handle such as @YouTube. Leave empty for the owner's own channel, which needs the sign-in from the Scout app.";

    private const string WatchUrl = "https://www.youtube.com/watch?v=";

    private const int MaxVideos = 50;

    private readonly IYouTubeClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>Overview of a channel, the owner's own when no handle is given.</summary>
    /// <param name="handle">Channel handle, or null for the owner's own channel.</param>
    /// <param name="ct">Cancels the underlying YouTube requests.</param>
    /// <returns>One fact per line, or a clean message when not found or on failure.</returns>
    [KernelFunction("youtube_channel_overview")]
    [Description("Overview of a YouTube channel: title, handle, subscribers, total views, video count and the date it was created. Without a handle it reads the owner's own channel.")]
    public async Task<string> ChannelOverviewAsync(
        [Description(HandleDescription)] string? handle = null,
        CancellationToken ct = default)
    {
        try
        {
            var channel = await ResolveChannelAsync(handle, ct).ConfigureAwait(false);
            return channel is null ? NoChannelLine(handle) : FormatChannel(channel);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>The latest uploads of a channel with their statistics.</summary>
    /// <param name="handle">Channel handle, or null for the owner's own channel.</param>
    /// <param name="count">How many uploads to list, 1 to 50.</param>
    /// <param name="ct">Cancels the underlying YouTube requests.</param>
    /// <returns>One video per line, or a clean message when none or on failure.</returns>
    [KernelFunction("youtube_recent_videos")]
    [Description("The latest uploads of a YouTube channel, newest first, one line each: published date, title, duration, views, likes, comments and the video link. Without a handle it reads the owner's own channel.")]
    public async Task<string> RecentVideosAsync(
        [Description(HandleDescription)] string? handle = null,
        [Description("How many of the latest uploads to list, from 1 to 50. Defaults to 10.")] int count = 10,
        CancellationToken ct = default)
    {
        try
        {
            var channel = await ResolveChannelAsync(handle, ct).ConfigureAwait(false);
            if (channel is null)
            {
                return NoChannelLine(handle);
            }

            if (string.IsNullOrWhiteSpace(channel.UploadsPlaylistId))
            {
                return $"{channel.Title} has no uploads.";
            }

            var videos = await _client.ListUploadsAsync(
                channel.UploadsPlaylistId, Math.Clamp(count, 1, MaxVideos), ct).ConfigureAwait(false);
            return videos.Count == 0
                ? $"{channel.Title} has no uploads."
                : string.Join("\n", videos.Select(FormatVideoLine));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Statistics of the videos named by id or link.</summary>
    /// <param name="videos">Video ids or links separated by commas or spaces.</param>
    /// <param name="ct">Cancels the underlying YouTube request.</param>
    /// <returns>One block per video and a line naming the ones not found, or a clean message on failure.</returns>
    [KernelFunction("youtube_video_stats")]
    [Description("Statistics of specific YouTube videos: title, channel, published date, duration, views, likes, comments and link. Accepts video ids or links.")]
    public async Task<string> VideoStatsAsync(
        [Description("One or more video ids or links (youtube.com/watch?v=..., youtu.be/..., youtube.com/shorts/...), separated by commas or spaces.")] string videos,
        CancellationToken ct = default)
    {
        var ids = new List<string>();
        var unrecognised = new List<string>();
        foreach (var token in (videos ?? string.Empty).Split(
            [',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var id = VideoId(token);
            (id is null ? unrecognised : ids).Add(id ?? token);
        }

        if (ids.Count == 0)
        {
            return "No YouTube video id or link found in the request.";
        }

        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        var asked = distinct.Take(MaxVideos).ToList();
        try
        {
            var found = await _client.GetVideosAsync(asked, ct).ConfigureAwait(false);
            var blocks = found.Select(FormatVideoBlock).ToList();
            var missing = asked
                .Where(id => found.All(v => v.Id != id))
                .Concat(unrecognised)
                .ToList();
            if (missing.Count > 0)
            {
                blocks.Add("Not found: " + string.Join(", ", missing));
            }

            if (distinct.Count > MaxVideos)
            {
                blocks.Add($"Not looked up (at most {MaxVideos} per request): " + string.Join(", ", distinct.Skip(MaxVideos)));
            }

            return string.Join("\n\n", blocks);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private Task<YouTubeChannel?> ResolveChannelAsync(string? handle, CancellationToken ct)
        => string.IsNullOrWhiteSpace(handle)
            ? _client.GetMyChannelAsync(ct)
            : _client.GetChannelByHandleAsync(handle, ct);

    private static string NoChannelLine(string? handle)
        => string.IsNullOrWhiteSpace(handle)
            ? "The signed-in account has no YouTube channel."
            : $"No YouTube channel has the handle @{handle.Trim().TrimStart('@')}.";

    private static string FormatChannel(YouTubeChannel channel)
        => string.Join("\n",
            $"Title: {channel.Title}",
            $"Handle: {channel.Handle ?? "(none)"}",
            $"Subscribers: {Number(channel.Subscribers)}",
            $"Total views: {Number(channel.Views)}",
            $"Videos: {Number(channel.VideoCount)}",
            $"Published: {Date(channel.PublishedAt)}");

    private static string FormatVideoLine(YouTubeVideo v)
        => $"{Date(v.PublishedAt)} | {v.Title} | {Duration(v.Duration)} | {Number(v.Views)} views | {Number(v.Likes)} likes | {Number(v.Comments)} comments | {WatchUrl}{v.Id}";

    private static string FormatVideoBlock(YouTubeVideo v)
        => string.Join("\n",
            v.Title,
            $"Channel: {v.ChannelTitle}",
            $"Published: {Date(v.PublishedAt)}",
            $"Duration: {Duration(v.Duration)}",
            $"Views: {Number(v.Views)}",
            $"Likes: {Number(v.Likes)}",
            $"Comments: {Number(v.Comments)}",
            $"URL: {WatchUrl}{v.Id}");

    private static string Number(long? value)
        => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "hidden";

    private static string Date(DateTimeOffset value)
        => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan value)
        => value.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes}:{value.Seconds:00}");

    private static string? VideoId(string token)
    {
        var match = VideoIdPattern().Match(token);
        return match.Success ? match.Groups["id"].Value : null;
    }

    [GeneratedRegex(@"^(?<id>[A-Za-z0-9_-]{11})$|(?:youtube\.com/watch\?(?:[^#]*&)?v=|youtu\.be/|youtube\.com/shorts/)(?<id>[A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])")]
    private static partial Regex VideoIdPattern();

    private static bool IsHandledFailure(Exception ex)
        => ex is YouTubeApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        YouTubeApiException api => api.Message,
        OperationCanceledException => "YouTube request timed out. Try again later.",
        _ => "Could not reach YouTube. Try again later.",
    };
}
