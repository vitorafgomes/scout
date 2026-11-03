using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Plugin.YouTube;

/// <summary>
/// Thin read-only YouTube Data API v3 client over an injected, pre-configured
/// <see cref="HttpClient"/> (base address, credential handler and headers are set by DI).
/// Non-success responses become a user-safe <see cref="YouTubeApiException"/> that never
/// carries the request URL, the key or Google's own message; timeouts and transport faults
/// bubble unchanged.
/// </summary>
internal sealed class YouTubeClient(HttpClient http) : IYouTubeClient
{
    internal const string SignInAgainMessage =
        "The YouTube sign-in is no longer valid. Sign in again in the Scout app.";

    internal const string MyChannelNeedsSignInMessage =
        "Sign in from the Scout app to read your own channel; an API key reads public channels by handle only.";

    internal const string QuotaMessage =
        "YouTube's daily quota for this key is spent; it resets at midnight Pacific time.";

    internal const string ForbiddenMessage = "The YouTube credentials cannot read this.";

    internal const string NotFoundMessage = "YouTube could not find that channel, playlist or video.";

    internal const string UnavailableMessage = "YouTube is currently unavailable. Try again later.";

    private const int MaxResults = 50;
    private const int MaxSearchResults = 25;
    private const string ChannelParts = "snippet,statistics,contentDetails";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<YouTubeChannel?> GetMyChannelAsync(CancellationToken ct)
    {
        var page = await GetAsync<ListDto<ChannelDto>>(
            $"channels?part={ChannelParts}&mine=true", ct, mine: true).ConfigureAwait(false);
        return ToChannel(page.Items?.FirstOrDefault());
    }

    public async Task<YouTubeChannel?> GetChannelByHandleAsync(string handle, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        var bare = Uri.EscapeDataString(handle.Trim().TrimStart('@'));
        var page = await GetAsync<ListDto<ChannelDto>>(
            $"channels?part={ChannelParts}&forHandle={bare}", ct).ConfigureAwait(false);
        return ToChannel(page.Items?.FirstOrDefault());
    }

    public async Task<IReadOnlyList<YouTubeVideo>> ListUploadsAsync(
        string uploadsPlaylistId, int max, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadsPlaylistId);
        var page = await GetAsync<ListDto<PlaylistItemDto>>(
            $"playlistItems?part=contentDetails&playlistId={Uri.EscapeDataString(uploadsPlaylistId)}&maxResults={Math.Clamp(max, 1, MaxResults)}",
            ct).ConfigureAwait(false);
        var ids = (page.Items ?? [])
            .Select(i => i.ContentDetails?.VideoId)
            .OfType<string>()
            .ToList();
        return await GetVideosAsync(ids, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<YouTubeVideo>> GetVideosAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Distinct(StringComparer.Ordinal).Take(MaxResults).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var page = await GetAsync<ListDto<VideoDto>>(
            $"videos?part=snippet,statistics,contentDetails&id={string.Join(',', wanted.Select(Uri.EscapeDataString))}",
            ct).ConfigureAwait(false);
        var byId = (page.Items ?? [])
            .Where(v => v.Id is not null)
            .DistinctBy(v => v.Id)
            .ToDictionary(v => v.Id!, StringComparer.Ordinal);
        return wanted.Where(byId.ContainsKey).Select(id => ToVideo(byId[id])).ToList();
    }

    public async Task<IReadOnlyList<YouTubeSearchResult>> SearchVideosAsync(string query, int max, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var page = await GetAsync<ListDto<SearchResultDto>>(
            $"search?part=snippet&type=video&order=relevance&maxResults={Math.Clamp(max, 1, MaxSearchResults)}&q={Uri.EscapeDataString(query.Trim())}",
            ct).ConfigureAwait(false);
        return (page.Items ?? [])
            .Where(i => i.Id?.VideoId is not null)
            .Select(i => new YouTubeSearchResult(
                VideoId: i.Id!.VideoId!,
                Title: i.Snippet?.Title ?? string.Empty,
                ChannelTitle: i.Snippet?.ChannelTitle ?? string.Empty,
                PublishedAt: i.Snippet?.PublishedAt ?? default,
                Description: i.Snippet?.Description ?? string.Empty))
            .ToList();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct, bool mine = false)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToApiExceptionAsync(response, mine, ct).ConfigureAwait(false);
        }

        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
            return value ?? throw new YouTubeApiException("YouTube returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new YouTubeApiException("YouTube returned an unexpected response.");
        }
    }

    private static async Task<YouTubeApiException> ToApiExceptionAsync(
        HttpResponseMessage response, bool mine, CancellationToken ct)
    {
        var status = response.StatusCode;
        if (mine && status is HttpStatusCode.Unauthorized)
        {
            return new YouTubeApiException(MyChannelNeedsSignInMessage, status);
        }

        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                return new YouTubeApiException(SignInAgainMessage, status);
            case HttpStatusCode.Forbidden:
                var forbiddenReason = await ReadReasonAsync(response, ct).ConfigureAwait(false);
                return forbiddenReason is "quotaExceeded" or "dailyLimitExceeded"
                    ? new YouTubeApiException(QuotaMessage, status)
                    : new YouTubeApiException(ForbiddenMessage, status);
            case HttpStatusCode.NotFound:
                return new YouTubeApiException(NotFoundMessage, status);
            case HttpStatusCode.BadRequest:
                var reason = await ReadReasonAsync(response, ct).ConfigureAwait(false);
                return new YouTubeApiException(
                    reason is null ? "YouTube rejected the request." : $"YouTube rejected the request ({reason}).",
                    status);
            default:
                return (int)status >= 500
                    ? new YouTubeApiException(UnavailableMessage, status)
                    : new YouTubeApiException($"YouTube request failed ({(int)status}).", status);
        }
    }

    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>(ct).ConfigureAwait(false);
            var reason = body?.Error?.Errors?.FirstOrDefault()?.Reason;
            return reason is { Length: > 0 and <= 64 } && reason.All(char.IsAsciiLetterOrDigit) ? reason : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static YouTubeChannel? ToChannel(ChannelDto? dto)
        => dto?.Id is null
            ? null
            : new YouTubeChannel(
                Id: dto.Id,
                Title: dto.Snippet?.Title ?? string.Empty,
                Handle: dto.Snippet?.CustomUrl,
                Subscribers: dto.Statistics is { HiddenSubscriberCount: false } s ? s.SubscriberCount : null,
                Views: dto.Statistics?.ViewCount ?? 0,
                VideoCount: dto.Statistics?.VideoCount ?? 0,
                PublishedAt: dto.Snippet?.PublishedAt ?? default,
                UploadsPlaylistId: dto.ContentDetails?.RelatedPlaylists?.Uploads);

    private static YouTubeVideo ToVideo(VideoDto dto)
        => new(
            Id: dto.Id!,
            Title: dto.Snippet?.Title ?? string.Empty,
            ChannelTitle: dto.Snippet?.ChannelTitle ?? string.Empty,
            PublishedAt: dto.Snippet?.PublishedAt ?? default,
            Duration: ParseDuration(dto.ContentDetails?.Duration),
            Views: dto.Statistics?.ViewCount,
            Likes: dto.Statistics?.LikeCount,
            Comments: dto.Statistics?.CommentCount);

    private static TimeSpan ParseDuration(string? iso8601)
    {
        if (string.IsNullOrWhiteSpace(iso8601))
        {
            return TimeSpan.Zero;
        }

        try
        {
            return XmlConvert.ToTimeSpan(iso8601);
        }
        catch (FormatException)
        {
            return TimeSpan.Zero;
        }
    }

    private sealed record ListDto<T>(
        [property: JsonPropertyName("items")] List<T>? Items);

    private sealed record ChannelDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("snippet")] ChannelSnippetDto? Snippet,
        [property: JsonPropertyName("statistics")] ChannelStatisticsDto? Statistics,
        [property: JsonPropertyName("contentDetails")] ChannelContentDetailsDto? ContentDetails);

    private sealed record ChannelSnippetDto(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("customUrl")] string? CustomUrl,
        [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt);

    private sealed record ChannelStatisticsDto(
        [property: JsonPropertyName("viewCount")] long? ViewCount,
        [property: JsonPropertyName("subscriberCount")] long? SubscriberCount,
        [property: JsonPropertyName("hiddenSubscriberCount")] bool HiddenSubscriberCount,
        [property: JsonPropertyName("videoCount")] long? VideoCount);

    private sealed record ChannelContentDetailsDto(
        [property: JsonPropertyName("relatedPlaylists")] RelatedPlaylistsDto? RelatedPlaylists);

    private sealed record RelatedPlaylistsDto(
        [property: JsonPropertyName("uploads")] string? Uploads);

    private sealed record PlaylistItemDto(
        [property: JsonPropertyName("contentDetails")] PlaylistItemContentDetailsDto? ContentDetails);

    private sealed record PlaylistItemContentDetailsDto(
        [property: JsonPropertyName("videoId")] string? VideoId);

    private sealed record VideoDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("snippet")] VideoSnippetDto? Snippet,
        [property: JsonPropertyName("statistics")] VideoStatisticsDto? Statistics,
        [property: JsonPropertyName("contentDetails")] VideoContentDetailsDto? ContentDetails);

    private sealed record VideoSnippetDto(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("channelTitle")] string? ChannelTitle,
        [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt);

    private sealed record VideoStatisticsDto(
        [property: JsonPropertyName("viewCount")] long? ViewCount,
        [property: JsonPropertyName("likeCount")] long? LikeCount,
        [property: JsonPropertyName("commentCount")] long? CommentCount);

    private sealed record VideoContentDetailsDto(
        [property: JsonPropertyName("duration")] string? Duration);

    private sealed record SearchResultDto(
        [property: JsonPropertyName("id")] SearchIdDto? Id,
        [property: JsonPropertyName("snippet")] SearchSnippetDto? Snippet);

    private sealed record SearchIdDto(
        [property: JsonPropertyName("videoId")] string? VideoId);

    private sealed record SearchSnippetDto(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("channelTitle")] string? ChannelTitle,
        [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt);

    private sealed record ErrorEnvelopeDto(
        [property: JsonPropertyName("error")] ErrorDto? Error);

    private sealed record ErrorDto(
        [property: JsonPropertyName("errors")] List<ErrorItemDto>? Errors);

    private sealed record ErrorItemDto(
        [property: JsonPropertyName("reason")] string? Reason);
}
