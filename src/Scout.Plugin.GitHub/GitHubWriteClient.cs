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
internal sealed class GitHubWriteClient : IGitHubWriteClient
{
    internal const string CommentEvent = "COMMENT";
    internal const string ApproveEvent = "APPROVE";
    internal const string RequestChangesEvent = "REQUEST_CHANGES";
    internal const string RightSide = "RIGHT";
    private const string NotValid = "the request was not valid";

    private readonly HttpClient _owner;
    private readonly HttpClient? _bot;
    private readonly BotSwitches _switches;

    /// <summary>Writes signed as the owner only.</summary>
    internal GitHubWriteClient(HttpClient owner)
        : this(owner, null, BotSwitches.None)
    {
    }

    /// <summary>Writes signed as the owner, or as the bot for the writes and repositories <paramref name="switches"/> name.</summary>
    internal GitHubWriteClient(HttpClient owner, HttpClient? bot, BotSwitches switches)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _bot = bot;
        _switches = switches ?? throw new ArgumentNullException(nameof(switches));
    }

    public GitHubSigner SignerFor(GitHubWriteKind kind, string owner, string repo) =>
        _bot is null ? BotSwitches.None.SignerFor(kind, owner, repo) : _switches.SignerFor(kind, owner, repo);

    private HttpClient For(GitHubWriteKind kind, string owner, string repo) =>
        SignerFor(kind, owner, repo).ByBot ? _bot! : _owner;

    public Task<PostedReview> SubmitReviewAsync(
        string owner, string repo, int number, string body, IReadOnlyList<ReviewComment> comments, CancellationToken ct) =>
        PostReviewAsync(owner, repo, number, CommentEvent, body, comments, null, For(GitHubWriteKind.Review, owner, repo), ct);

    public Task<PostedReview> SubmitVerdictAsync(
        string owner, string repo, int number, ReviewVerdict verdict, string body, IReadOnlyList<ReviewComment> comments, string commitId, CancellationToken ct) =>
        PostReviewAsync(owner, repo, number, verdict == ReviewVerdict.Approve ? ApproveEvent : RequestChangesEvent, body, comments, commitId, For(GitHubWriteKind.Verdict, owner, repo), ct);

    public Task<PostedComment> CommentOnIssueAsync(string owner, string repo, int number, string body, CancellationToken ct) =>
        CommentOnIssueAsync(owner, repo, number, body, GitHubWriteKind.Comment, ct);

    public async Task<PostedComment> CommentOnIssueAsync(
        string owner, string repo, int number, string body, GitHubWriteKind signedAs, CancellationToken ct)
    {
        var reply = await PostAsync<CommentRequestDto, CommentReplyDto>(
                For(signedAs, owner, repo), $"repos/{owner}/{repo}/issues/{number}/comments", new CommentRequestDto(body), ct)
            .ConfigureAwait(false);
        return new PostedComment(reply.Id, reply.HtmlUrl ?? string.Empty);
    }

    public async Task<PostedComment> ReplyToReviewCommentAsync(
        string owner, string repo, int number, long commentId, string body, CancellationToken ct)
    {
        var reply = await PostAsync<CommentRequestDto, CommentReplyDto>(
                For(GitHubWriteKind.Comment, owner, repo), $"repos/{owner}/{repo}/pulls/{number}/comments/{commentId}/replies", new CommentRequestDto(body), ct)
            .ConfigureAwait(false);
        return new PostedComment(reply.Id, reply.HtmlUrl ?? string.Empty);
    }

    private static async Task<PostedReview> PostReviewAsync(
        string owner, string repo, int number, string reviewEvent, string body, IReadOnlyList<ReviewComment> comments, string? commitId, HttpClient http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(comments);
        var request = new ReviewRequestDto(
            body,
            reviewEvent,
            comments.Select(c => new ReviewCommentDto(c.Path, c.Line, RightSide, c.Body)).ToList(),
            commitId);

        var reply = await PostAsync<ReviewRequestDto, ReviewReplyDto>(http, $"repos/{owner}/{repo}/pulls/{number}/reviews", request, ct)
            .ConfigureAwait(false);
        return new PostedReview(reply.Id, reply.HtmlUrl ?? string.Empty);
    }

    public async Task<CreatedIssue> CreateIssueAsync(
        string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var request = new IssueRequestDto(title, body, labels.Count == 0 ? null : labels);

        var reply = await PostAsync<IssueRequestDto, IssueReplyDto>(For(GitHubWriteKind.Issue, owner, repo), $"repos/{owner}/{repo}/issues", request, ct)
            .ConfigureAwait(false);
        return new CreatedIssue(reply.Number, reply.HtmlUrl ?? string.Empty);
    }

    private static async Task<TReply> PostAsync<TRequest, TReply>(HttpClient http, string path, TRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var reason = Rejection(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            throw new GitHubApiException($"GitHub rejected the write: {reason}.", response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await GitHubClient.ToApiExceptionAsync(response, ct).ConfigureAwait(false);
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
        [property: JsonPropertyName("comments")] IReadOnlyList<ReviewCommentDto> Comments,
        [property: JsonPropertyName("commit_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommitId);

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

/// <summary>The writes and the repositories the owner switched to the Scout bot, and the bot's login.</summary>
/// <param name="Kinds">The kinds of write the bot signs.</param>
/// <param name="Repositories">The <c>owner/repo</c> the bot's installation covers, compared without case.</param>
/// <param name="Login">The bot's login, or empty when unknown.</param>
internal sealed record BotSwitches(IReadOnlySet<GitHubWriteKind> Kinds, IReadOnlySet<string> Repositories, string Login)
{
    internal static readonly BotSwitches None = new(new HashSet<GitHubWriteKind>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), string.Empty);

    /// <summary>
    /// Who signs a write of <paramref name="kind"/> on <paramref name="owner"/>/<paramref name="repo"/>: the bot when the
    /// owner switched the kind to it and its installation covers the repository, otherwise the owner, saying when the bot
    /// would have signed but is not installed there.
    /// </summary>
    internal GitHubSigner SignerFor(GitHubWriteKind kind, string owner, string repo)
    {
        if (!Kinds.Contains(kind))
        {
            return new GitHubSigner(false, null, false);
        }

        return Repositories.Contains($"{owner}/{repo}")
            ? new GitHubSigner(true, Login.Length == 0 ? null : Login, false)
            : new GitHubSigner(false, null, true);
    }

    /// <summary>Read the comma-separated kinds and repositories as the desktop writes them; an unknown kind is ignored.</summary>
    internal static BotSwitches Parse(string? kinds, string? repositories, string? login) => new(
        Split(kinds)
            .Select(k => k.ToLowerInvariant() switch
            {
                "review" => GitHubWriteKind.Review,
                "verdict" => GitHubWriteKind.Verdict,
                "comment" => GitHubWriteKind.Comment,
                "issue" => GitHubWriteKind.Issue,
                _ => (GitHubWriteKind?)null,
            })
            .OfType<GitHubWriteKind>()
            .ToHashSet(),
        Split(repositories).ToHashSet(StringComparer.OrdinalIgnoreCase),
        (login ?? string.Empty).Trim());

    private static string[] Split(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
