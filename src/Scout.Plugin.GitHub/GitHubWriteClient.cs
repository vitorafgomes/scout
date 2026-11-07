using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// The POSTs Scout makes to GitHub, over the same configured <see cref="HttpClient"/> shape as the reads:
/// <c>pulls/{n}/reviews</c> with the event <c>COMMENT</c>, <c>APPROVE</c> or <c>REQUEST_CHANGES</c> and every comment
/// on the right side of the diff, <c>issues</c>, <c>issues/{n}/comments</c> and
/// <c>pulls/{n}/comments/{id}/replies</c>. A 422 names what GitHub rejected; every other failure maps as the reads do.
/// </summary>
internal sealed class GitHubWriteClient(HttpClient http) : IGitHubWriteClient
{
    internal const string CommentEvent = "COMMENT";
    internal const string ApproveEvent = "APPROVE";
    internal const string RequestChangesEvent = "REQUEST_CHANGES";
    internal const string RightSide = "RIGHT";
    private const string NotValid = "the request was not valid";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public Task<PostedReview> SubmitReviewAsync(
        string owner, string repo, int number, string body, IReadOnlyList<ReviewComment> comments, CancellationToken ct) =>
        PostReviewAsync(owner, repo, number, CommentEvent, body, comments, ct);

    public Task<PostedReview> SubmitVerdictAsync(
        string owner, string repo, int number, ReviewVerdict verdict, string body, IReadOnlyList<ReviewComment> comments, CancellationToken ct) =>
        PostReviewAsync(owner, repo, number, verdict == ReviewVerdict.Approve ? ApproveEvent : RequestChangesEvent, body, comments, ct);

    public async Task<PostedComment> CommentOnIssueAsync(string owner, string repo, int number, string body, CancellationToken ct)
    {
        var reply = await PostAsync<CommentRequestDto, CommentReplyDto>($"repos/{owner}/{repo}/issues/{number}/comments", new CommentRequestDto(body), ct)
            .ConfigureAwait(false);
        return new PostedComment(reply.Id, reply.HtmlUrl ?? string.Empty);
    }

    public async Task<PostedComment> ReplyToReviewCommentAsync(
        string owner, string repo, int number, long commentId, string body, CancellationToken ct)
    {
        var reply = await PostAsync<CommentRequestDto, CommentReplyDto>(
                $"repos/{owner}/{repo}/pulls/{number}/comments/{commentId}/replies", new CommentRequestDto(body), ct)
            .ConfigureAwait(false);
        return new PostedComment(reply.Id, reply.HtmlUrl ?? string.Empty);
    }

    private async Task<PostedReview> PostReviewAsync(
        string owner, string repo, int number, string reviewEvent, string body, IReadOnlyList<ReviewComment> comments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(comments);
        var request = new ReviewRequestDto(
            body,
            reviewEvent,
            comments.Select(c => new ReviewCommentDto(c.Path, c.Line, RightSide, c.Body)).ToList());

        var reply = await PostAsync<ReviewRequestDto, ReviewReplyDto>($"repos/{owner}/{repo}/pulls/{number}/reviews", request, ct)
            .ConfigureAwait(false);
        return new PostedReview(reply.Id, reply.HtmlUrl ?? string.Empty);
    }

    public async Task<CreatedIssue> CreateIssueAsync(
        string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var request = new IssueRequestDto(title, body, labels.Count == 0 ? null : labels);

        var reply = await PostAsync<IssueRequestDto, IssueReplyDto>($"repos/{owner}/{repo}/issues", request, ct)
            .ConfigureAwait(false);
        return new CreatedIssue(reply.Number, reply.HtmlUrl ?? string.Empty);
    }

    private async Task<TReply> PostAsync<TRequest, TReply>(string path, TRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var reason = Rejection(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            throw new GitHubApiException($"GitHub rejected the write: {reason}.", response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw GitHubClient.ToApiException(response);
        }

        return await response.Content.ReadFromJsonAsync<TReply>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new GitHubApiException("GitHub returned an unexpected empty response.");
    }

    /// <summary>What a 422 names: its errors, each a text or an object with a message, else its message.</summary>
    private static string Rejection(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NotValid;
            }

            var reasons = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                ? errors.EnumerateArray()
                    .Select(e => e.ValueKind switch
                    {
                        JsonValueKind.String => e.GetString(),
                        JsonValueKind.Object when e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString(),
                        _ => null,
                    })
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .ToList()
                : [];
            if (reasons.Count > 0)
            {
                return string.Join("; ", reasons);
            }

            return root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()!
                : NotValid;
        }
        catch (JsonException)
        {
            return NotValid;
        }
    }

    private sealed record ReviewRequestDto(
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("comments")] IReadOnlyList<ReviewCommentDto> Comments);

    private sealed record ReviewCommentDto(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("side")] string Side,
        [property: JsonPropertyName("body")] string Body);

    private sealed record ReviewReplyDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);

    private sealed record IssueRequestDto(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("labels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Labels);

    private sealed record IssueReplyDto(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);

    private sealed record CommentRequestDto([property: JsonPropertyName("body")] string Body);

    private sealed record CommentReplyDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}
