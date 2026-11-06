using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// The two POSTs Scout makes to GitHub, over the same configured <see cref="HttpClient"/> as the reads:
/// <c>pulls/{n}/reviews</c> with the event <c>COMMENT</c> and every comment on the right side of the diff, and
/// <c>issues</c>. A 422 names what GitHub rejected; every other failure maps as the reads do.
/// </summary>
internal sealed class GitHubWriteClient(HttpClient http) : IGitHubWriteClient
{
    internal const string CommentEvent = "COMMENT";
    internal const string RightSide = "RIGHT";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<PostedReview> SubmitReviewAsync(
        string owner, string repo, int number, string body, IReadOnlyList<ReviewComment> comments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(comments);
        var request = new ReviewRequestDto(
            body,
            CommentEvent,
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
            var detail = await response.Content.ReadFromJsonAsync<ErrorDto>(cancellationToken: ct).ConfigureAwait(false);
            var reasons = detail?.Errors?.Select(e => e.Message).OfType<string>().ToList() ?? [];
            var reason = reasons.Count > 0 ? string.Join("; ", reasons) : detail?.Message ?? "the request was not valid";
            throw new GitHubApiException($"GitHub rejected the write: {reason}.", response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw GitHubClient.ToApiException(response);
        }

        return await response.Content.ReadFromJsonAsync<TReply>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new GitHubApiException("GitHub returned an unexpected empty response.");
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

    private sealed record ErrorDto(
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("errors")] List<ErrorDetailDto>? Errors);

    private sealed record ErrorDetailDto(
        [property: JsonPropertyName("message")] string? Message);
}
