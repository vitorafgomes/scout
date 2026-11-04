using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Plugin.YouTube;

/// <summary>
/// The YouTube writes over an injected <see cref="HttpClient"/> whose base address is <c>https://www.googleapis.com/</c> and
/// whose handler sets the owner's bearer token. An upload uses the resumable protocol: a session opened with the metadata,
/// the bytes sent to the session address, and after a server error or a dropped connection a query for the bytes YouTube
/// kept and the rest sent; a PUT that stalls past <see cref="StallLimit"/> is resumed the same way; a session YouTube no
/// longer knows starts over; the session lives in the caller's <see cref="UploadSession"/>, so a later call resumes it. A session address outside googleapis.com is
/// refused, so the token and the video never go elsewhere. Failures are <see cref="YouTubeApiException"/>s with a
/// sentence the owner can read.
/// </summary>
internal sealed class YouTubeWriteClient(HttpClient http, TimeProvider time) : IYouTubeWriteClient
{
    internal const int MaxAttempts = 5;

    internal const long MaxThumbnailBytes = 50L * 1024 * 1024;

    internal const string UploadInterrupted = "The upload to YouTube kept failing; Scout tries again later.";

    internal const string UnexpectedSession = "YouTube answered the upload with an address Scout does not trust; nothing was sent there.";

    internal const string AngleBrackets = "YouTube does not accept < or > in a title or description; edit the metadata in Scout Desktop.";

    internal const string ThumbnailForbidden =
        "YouTube refused the thumbnail: custom thumbnails need a verified channel (verify it at youtube.com/verify), or the sign-in does not allow Scout to change this video.";

    internal const string RateLimited = "YouTube's upload rate limit is reached for now; Scout tries again later.";

    internal const string ThumbnailTooLarge = "The thumbnail is larger than YouTube's 50 MB limit.";

    internal const string MissingPermission =
        "YouTube needs the upload permission: open the YouTube card in Scout Desktop and allow writes again.";

    internal const string WriteForbidden = "The YouTube sign-in does not allow Scout to change this video; sign in again in Scout Desktop.";

    internal const string CommentForbidden =
        "YouTube refused the change to this comment: it may have been deleted, or the sign-in does not allow Scout to change it.";

    internal const string MissingCommentPermission =
        "YouTube needs Scout's permission to reply to and moderate comments: open the YouTube card in Scout Desktop and allow writes again.";

    internal const string NoReplyId = "YouTube took the reply but returned no id for it.";

    private const string GoogleApisHost = "www.googleapis.com";
    private const string VideoType = "video/mp4";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The wait before the second attempt, doubled before each later one.</summary>
    internal TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<string> UploadVideoAsync(string filePath, YouTubeUpload upload, UploadSession session, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(session);
        if (HasAngleBrackets(upload.Title) || HasAngleBrackets(upload.Description))
        {
            throw new YouTubeApiException(AngleBrackets, HttpStatusCode.BadRequest);
        }

        var total = new FileInfo(filePath).Length;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(Backoff * (1 << (attempt - 2)), time, ct).ConfigureAwait(false);
            }

            try
            {
                long offset = 0;
                if (session.Address is { } open)
                {
                    var progress = await QueryAsync(open, total, ct).ConfigureAwait(false);
                    if (progress.VideoId is { } done)
                    {
                        return done;
                    }

                    if (progress.Gone)
                    {
                        session.Address = null;
                    }

                    offset = progress.Offset;
                }

                if (session.Address is null)
                {
                    session.Address = await OpenSessionAsync(upload, total, ct).ConfigureAwait(false);
                    offset = 0;
                }

                using var sent = await SendBytesAsync(session.Address, filePath, offset, total, ct).ConfigureAwait(false);
                if (sent.IsSuccessStatusCode)
                {
                    return await VideoIdAsync(sent, ct).ConfigureAwait(false);
                }

                if (sent.StatusCode == HttpStatusCode.NotFound)
                {
                    session.Address = null;
                    continue;
                }

                if ((int)sent.StatusCode == 308)
                {
                    continue;
                }

                if ((int)sent.StatusCode < 500)
                {
                    throw await YouTubeClient.ToApiExceptionAsync(sent, mine: false, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (Interrupted(ex, ct))
            {
            }
        }

        throw new YouTubeApiException(UploadInterrupted, HttpStatusCode.ServiceUnavailable) { Retryable = true };
    }

    public async Task SetThumbnailAsync(string videoId, string filePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (new FileInfo(filePath).Length > MaxThumbnailBytes)
        {
            throw new YouTubeApiException(ThumbnailTooLarge, HttpStatusCode.RequestEntityTooLarge);
        }

        var type = Path.GetExtension(filePath).ToUpperInvariant() is ".JPG" or ".JPEG" ? "image/jpeg" : "image/png";
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false));
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        using var response = await http.PostAsync(
            $"upload/youtube/v3/thumbnails/set?videoId={Uri.EscapeDataString(videoId)}", content, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw await WriteFailureAsync(response, ct, ThumbnailForbidden).ConfigureAwait(false);
    }

    public async Task UpdateTitleAsync(string videoId, string title, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (HasAngleBrackets(title))
        {
            throw new YouTubeApiException(AngleBrackets, HttpStatusCode.BadRequest);
        }

        JsonObject snippet;
        using (var read = await http.GetAsync($"youtube/v3/videos?part=snippet&id={Uri.EscapeDataString(videoId)}", ct).ConfigureAwait(false))
        {
            if (!read.IsSuccessStatusCode)
            {
                throw await YouTubeClient.ToApiExceptionAsync(read, mine: false, ct).ConfigureAwait(false);
            }

            var page = await ReadAsync<JsonObject>(read, ct).ConfigureAwait(false);
            snippet = page["items"] is JsonArray { Count: > 0 } items && items[0]?["snippet"] is JsonObject found
                ? found
                : throw new YouTubeApiException(YouTubeClient.NotFoundMessage, HttpStatusCode.NotFound);
        }

        if (snippet["title"]?.GetValue<string>() == title)
        {
            return;
        }

        var resent = new JsonObject { ["title"] = title, ["categoryId"] = snippet["categoryId"]?.DeepClone() };
        foreach (var kept in new[] { "description", "tags", "defaultLanguage" })
        {
            if (snippet[kept] is { } value)
            {
                resent[kept] = value.DeepClone();
            }
        }

        using var response = await http.PutAsJsonAsync(
            "youtube/v3/videos?part=snippet", new JsonObject { ["id"] = videoId, ["snippet"] = resent }, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await WriteFailureAsync(response, ct).ConfigureAwait(false);
        }
    }

    public async Task<YouTubeVideoStatus?> GetVideoStatusAsync(string videoId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        using var response = await http.GetAsync($"youtube/v3/videos?part=status&id={Uri.EscapeDataString(videoId)}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await YouTubeClient.ToApiExceptionAsync(response, mine: false, ct).ConfigureAwait(false);
        }

        var page = await ReadAsync<StatusPageDto>(response, ct).ConfigureAwait(false);
        return page.Items?.FirstOrDefault()?.Status is { } status
            ? new YouTubeVideoStatus(status.UploadStatus ?? string.Empty, status.PrivacyStatus ?? string.Empty, status.PublishAt)
            : null;
    }

    public async Task<string> ReplyToCommentAsync(string parentId, string text, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        using var response = await http.PostAsJsonAsync(
            "youtube/v3/comments?part=snippet", new ReplyDto(new ReplySnippetDto(parentId, text)), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await WriteFailureAsync(response, ct, CommentForbidden, MissingCommentPermission).ConfigureAwait(false);
        }

        VideoIdDto reply;
        try
        {
            reply = await ReadAsync<VideoIdDto>(response, ct).ConfigureAwait(false);
        }
        catch (YouTubeApiException unreadable)
        {
            throw new YouTubeApiException(unreadable.Message, response.StatusCode);
        }

        return string.IsNullOrWhiteSpace(reply.Id) ? throw new YouTubeApiException(NoReplyId, response.StatusCode) : reply.Id;
    }

    public async Task HoldCommentAsync(string commentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentId);
        using var response = await http.PostAsync(
            $"youtube/v3/comments/setModerationStatus?id={Uri.EscapeDataString(commentId)}&moderationStatus=heldForReview", content: null, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await WriteFailureAsync(response, ct, CommentForbidden, MissingCommentPermission).ConfigureAwait(false);
        }
    }

    private async Task<Uri> OpenSessionAsync(YouTubeUpload upload, long total, CancellationToken ct)
    {
        var resource = new VideoResourceDto(
            new SnippetDto(upload.Title, upload.Description, upload.Tags, upload.CategoryId, upload.DefaultLanguage),
            new StatusDto(
                upload.PrivacyStatus,
                upload.PublishAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                upload.MadeForKids,
                upload.SyntheticMedia));
        using var request = new HttpRequestMessage(HttpMethod.Post, "upload/youtube/v3/videos?uploadType=resumable&part=snippet,status")
        {
            Content = JsonContent.Create(resource, options: Json),
        };
        request.Headers.Add("X-Upload-Content-Length", total.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Upload-Content-Type", VideoType);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await WriteFailureAsync(response, ct).ConfigureAwait(false);
        }

        var location = response.Headers.Location;
        if (location is null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps || location.Host != GoogleApisHost)
        {
            throw new YouTubeApiException(UnexpectedSession);
        }

        return location;
    }

    /// <summary>How long one PUT may take before it counts as stalled: 5 minutes plus the bytes left at 100 KB a second.</summary>
    internal static TimeSpan StallLimit(long remainingBytes) => TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(remainingBytes / 102_400.0);

    private async Task<HttpResponseMessage> SendBytesAsync(Uri session, string filePath, long offset, long total, CancellationToken ct)
    {
        using var stall = new CancellationTokenSource(StallLimit(total - offset), time);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
        var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        file.Seek(offset, SeekOrigin.Begin);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(VideoType);
        content.Headers.ContentLength = total - offset;
        content.Headers.ContentRange = new ContentRangeHeaderValue(offset, total - 1, total);
        using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
        return await http.SendAsync(request, attempt.Token).ConfigureAwait(false);
    }

    private async Task<UploadProgress> QueryAsync(Uri session, long total, CancellationToken ct)
    {
        using var content = new ByteArrayContent([]);
        content.Headers.ContentRange = new ContentRangeHeaderValue(total);
        using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return new UploadProgress(await VideoIdAsync(response, ct).ConfigureAwait(false), 0, Gone: false);
        }

        switch ((int)response.StatusCode)
        {
            case 308:
                return new UploadProgress(null, KeptBytes(response), Gone: false);
            case 404:
                return new UploadProgress(null, 0, Gone: true);
            case >= 500:
                throw new HttpRequestException(YouTubeClient.UnavailableMessage, null, response.StatusCode);
            default:
                throw await YouTubeClient.ToApiExceptionAsync(response, mine: false, ct).ConfigureAwait(false);
        }
    }

    private static long KeptBytes(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values))
        {
            return 0;
        }

        var range = values.FirstOrDefault() ?? string.Empty;
        var dash = range.LastIndexOf('-');
        return dash >= 0 && long.TryParse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            ? last + 1
            : 0;
    }

    private static async Task<string> VideoIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var video = await ReadAsync<VideoIdDto>(response, ct).ConfigureAwait(false);
        return video.Id is { Length: > 0 } id ? id : throw new YouTubeApiException("YouTube returned an unexpected response.");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false)
                ?? throw new YouTubeApiException("YouTube returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new YouTubeApiException("YouTube returned an unexpected response.");
        }
    }

    private static async Task<YouTubeApiException> WriteFailureAsync(
        HttpResponseMessage response, CancellationToken ct, string forbidden = WriteForbidden, string missingPermission = MissingPermission)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new YouTubeApiException(RateLimited, response.StatusCode) { Retryable = true };
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return await YouTubeClient.ToApiExceptionAsync(response, mine: false, ct).ConfigureAwait(false);
        }

        return await YouTubeClient.ReadReasonAsync(response, ct).ConfigureAwait(false) switch
        {
            "insufficientPermissions" => new YouTubeApiException(missingPermission, response.StatusCode) { Retryable = true },
            "quotaExceeded" or "dailyLimitExceeded" => new YouTubeApiException(YouTubeClient.QuotaMessage, response.StatusCode) { Retryable = true },
            _ => new YouTubeApiException(forbidden, response.StatusCode),
        };
    }

    private static bool Interrupted(Exception ex, CancellationToken ct)
        => !ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException;

    private static bool HasAngleBrackets(string text) => text.Contains('<', StringComparison.Ordinal) || text.Contains('>', StringComparison.Ordinal);

    private sealed record UploadProgress(string? VideoId, long Offset, bool Gone);

    private sealed record VideoResourceDto(
        [property: JsonPropertyName("snippet")] SnippetDto Snippet,
        [property: JsonPropertyName("status")] StatusDto Status);

    private sealed record SnippetDto(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
        [property: JsonPropertyName("categoryId")] string CategoryId,
        [property: JsonPropertyName("defaultLanguage")] string? DefaultLanguage);

    private sealed record StatusDto(
        [property: JsonPropertyName("privacyStatus")] string PrivacyStatus,
        [property: JsonPropertyName("publishAt")] string? PublishAt,
        [property: JsonPropertyName("selfDeclaredMadeForKids")] bool SelfDeclaredMadeForKids,
        [property: JsonPropertyName("containsSyntheticMedia")] bool ContainsSyntheticMedia);

    private sealed record ReplyDto([property: JsonPropertyName("snippet")] ReplySnippetDto Snippet);

    private sealed record ReplySnippetDto(
        [property: JsonPropertyName("parentId")] string ParentId,
        [property: JsonPropertyName("textOriginal")] string TextOriginal);

    private sealed record VideoIdDto([property: JsonPropertyName("id")] string? Id);

    private sealed record StatusPageDto([property: JsonPropertyName("items")] List<StatusItemDto>? Items);

    private sealed record StatusItemDto([property: JsonPropertyName("status")] VideoStatusDto? Status);

    private sealed record VideoStatusDto(
        [property: JsonPropertyName("uploadStatus")] string? UploadStatus,
        [property: JsonPropertyName("privacyStatus")] string? PrivacyStatus,
        [property: JsonPropertyName("publishAt")] DateTimeOffset? PublishAt);
}
