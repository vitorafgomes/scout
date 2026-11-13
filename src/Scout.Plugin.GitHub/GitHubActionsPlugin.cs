using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// The writes the model may ask for around GitHub, each declared on its own row of the <c>github</c> feature and run
/// only under the owner's grant: <c>submit_pull_request_review</c> posts a commenting review on a pull request,
/// <c>create_issue</c> opens an issue, <c>comment_on_pull_request</c> and <c>comment_on_issue</c> comment on a
/// conversation, <c>reply_to_review_comment</c> answers a review comment and <c>give_pull_request_verdict</c> approves a
/// pull request or requests changes: each of these is an irreversible write the gate queues, with its arguments, for
/// the owner's approval of that one action, so a call by the model sends nothing; an approved review, issue, comment
/// or reply is sent by <see cref="GitHubWriteRunner"/> through <see cref="PostReviewAsync"/>,
/// <see cref="OpenIssueAsync"/>, <see cref="CommentAsync"/> and <see cref="ReplyAsync"/>. The write gate refuses any of
/// them before anything is sent while the owner has not allowed GitHub writes, and records every run.
/// <c>remember_project_conventions</c> is a write into Scout's data folder: the conventions profile of a repository
/// an audit judges the code against, read back with <c>get_project_conventions</c>; <c>snapshot_repository</c> is
/// another: the repository at one commit, extracted under that folder so every read of it is local and the Knowledge
/// index can search it. The model is told what GitHub accepted, never more: a review or an issue that was not
/// posted is a <see cref="WriteFailedException"/>, which the write gate filter turns into the answer and records as
/// failed.
/// </summary>
internal sealed partial class GitHubActionsPlugin(
    IGitHubClient reads,
    IGitHubWriteClient writes,
    GitHubProfileStore profiles,
    RepositorySnapshotStore snapshots,
    IRepositoryProjects projects,
    TimeProvider time,
    IRepositoryAudits? audits = null,
    ISandboxRunner? sandbox = null,
    TestRuns? testRuns = null,
    IStackDetector? stacks = null,
    ILogger<GitHubActionsPlugin>? logger = null,
    RunningTests? running = null)
{
    internal const string PluginName = "GitHubActions";
    internal const string SubmitReview = "submit_pull_request_review";
    internal const string CreateIssue = "create_issue";
    internal const string RememberConventions = "remember_project_conventions";
    internal const string GetConventions = "get_project_conventions";
    internal const string SnapshotRepository = "snapshot_repository";
    internal const string GiveVerdict = "give_pull_request_verdict";
    internal const string StartAudit = "start_repository_audit";
    internal const string StopAudit = "stop_repository_audit";
    internal const string PauseAudit = "pause_repository_audit";
    internal const string AuditStatus = "get_repository_audit_status";
    internal const string NoAudits = "Audits are not available in this Scout.";
    internal const string CommentOnPullRequest = "comment_on_pull_request";
    internal const string CommentOnIssue = "comment_on_issue";
    internal const string ReplyToReviewComment = "reply_to_review_comment";

    internal const int MaxConventionsLength = 20_000;
    internal const int MinConventionsLength = 40;

    /// <summary>The catalog id of the GitHub feature.</summary>
    internal const string FeatureId = "github";

    /// <summary>The catalog row the conventions profile is saved under.</summary>
    internal const string DataFolderScope = "Scout's data folder";

    /// <summary>The catalog row a posted review performs.</summary>
    internal const string ReviewScope = "Pull request reviews";

    /// <summary>The catalog row an opened issue performs.</summary>
    internal const string IssueScope = "New issues";

    /// <summary>The catalog row an approval or a request for changes performs; irreversible, so each waits for the owner.</summary>
    internal const string VerdictScope = "Pull request verdicts";

    /// <summary>The catalog row a comment on a pull request's conversation, or a reply to a review comment, performs.</summary>
    internal const string PullRequestCommentScope = "Pull request comments";

    /// <summary>The catalog row a comment on an issue performs.</summary>
    internal const string IssueCommentScope = "Issue comments";

    internal const int MaxConversationCommentLength = 65_000;
    internal const string EmptyComment = "The comment is empty.";
    internal const string CommentTooLong = "The comment is over 65,000 characters.";
    internal const string InvalidVerdict = "The verdict must be approve or request_changes; a review that only comments is submit_pull_request_review.";
    internal const string BotLoginUnknown = "The Scout bot's login is unknown; give the verdict as yourself or recreate the bot.";
    internal const string HeadMoved = "The pull request changed since the review; review it again.";
    internal const string NoCommit = "The verdict needs the head commit the review read, at least 7 characters of its SHA.";
    internal const string SelfApproval = "GitHub does not let the author approve their own pull request; request changes or comment instead.";

    internal const int MaxComments = 50;
    internal const int MaxBodyLength = 10_000;
    internal const int MaxCommentLength = 2_000;
    internal const int MaxTitleLength = 256;
    internal const int MaxLabels = 10;

    internal const string NothingToPost = "Nothing to post: the review has no body and no comment on a line of the diff.";
    internal const string InvalidComments =
        "The comments must be a JSON array of objects with \"path\", \"line\" (a positive integer) and \"body\".";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Post a commenting review on a pull request; an irreversible write that runs only after the owner approved this review.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="body">The review's summary text; may be empty when there are comments.</param>
    /// <param name="comments">JSON array of comments: path, line (in the file after the change), body.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>That the review was posted, with its URL and the comments dropped for pointing outside the diff, or why it was refused.</returns>
    [KernelFunction(SubmitReview)]
    [Description("Ask for a code review to be posted on a pull request: a summary and comments anchored to lines of the pull request's diff, as a commenting review (never an approval or a request for changes). Only after the user asked in chat for the review to be posted; the review card's Post button does not go through chat. It is queued for the owner's approval, an Approve card waiting in the chat and in Approvals of Scout Desktop, and sent only after the owner approves it; say that it waits for the owner's approval. Never call it again for the same review.")]
    public async Task<string> SubmitPullRequestReviewAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number.")] int number,
        [Description("The review's summary, in Markdown; empty when the comments say it all.")] string body,
        [Description("""JSON array of comments, e.g. [{"path":"src/Checkout.cs","line":88,"body":"`total` is summed before the discounts apply."}]; line is the line number in the file after the change, as the diff shows it.""")] string comments,
        CancellationToken ct) =>
        (await PostReviewAsync(owner, repo, number, body, comments, ct).ConfigureAwait(false)).Answer;

    /// <summary>
    /// Post the commenting review on the pull request, refused before anything is sent when a value is wrong.
    /// </summary>
    internal async Task<(PostedReview Posted, string Answer)> PostReviewAsync(
        string owner, string repo, int number, string? body, string? comments, CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            throw Refused(GitHubNames.InvalidRepository);
        }

        if (number <= 0)
        {
            throw Refused("The pull request number must be positive.");
        }

        var requested = CheckReview(comments, body, out var summary);
        var (kept, dropped) = await AnchorOnTheDiffAsync(owner, repo, number, requested, summary, ct).ConfigureAwait(false);

        PostedReview posted;
        try
        {
            posted = await writes.SubmitReviewAsync(owner, repo, number, summary, kept, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: true);
        }

        return (posted, Posted($"Posted a review on {owner}/{repo}#{number.ToString(CultureInfo.InvariantCulture)}", kept, dropped, posted)
            + Signed(GitHubWriteKind.Review, owner, repo));
    }

    /// <summary>Approve a pull request or request changes on it; an irreversible write that runs only after the owner approved this action.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="verdict"><c>approve</c> or <c>request_changes</c>.</param>
    /// <param name="body">The review's summary text; may be empty when there are comments.</param>
    /// <param name="comments_json">JSON array of comments: path, line (in the file after the change), body.</param>
    /// <param name="commit">The head commit the review read.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>That the verdict was given, with its URL and the comments dropped for pointing outside the diff, or why it was refused.</returns>
    [KernelFunction(GiveVerdict)]
    [Description("Approve a pull request or request changes on it, with a summary and comments anchored to lines of the pull request's diff, pinned to the head commit the review read. Call it only when the user asks for that verdict in chat, never on your own judgement; the review card's Approve and Request changes buttons do not go through chat. The verdict is queued for the owner's approval, an Approve card waiting in the chat and in Approvals of Scout Desktop, and sent after the owner approves it; it is refused when the pull request moved past the commit reviewed. GitHub does not let the author approve their own pull request.")]
    public async Task<string> GivePullRequestVerdictAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number.")] int number,
        [Description("approve or request_changes.")] string verdict,
        [Description("The review's summary, in Markdown; empty when the comments say it all.")] string body,
        [Description("""JSON array of comments, e.g. [{"path":"src/Checkout.cs","line":88,"body":"`total` is summed before the discounts apply."}]; line is the line number in the file after the change, as the diff shows it.""")] string comments_json,
        [Description("The head commit the review read, e.g. ec7f951.")] string commit,
        CancellationToken ct) =>
        (await GiveVerdictAsync(owner, repo, number, verdict, body, comments_json, commit, ct).ConfigureAwait(false)).Answer;

    /// <summary>
    /// Give the verdict on the pull request, refused before anything is sent when a value is wrong, the pull request moved
    /// past <paramref name="commit"/>, or the identity that signs it opened the pull request.
    /// </summary>
    internal async Task<(PostedReview Posted, string Answer, bool Already, string? Signer)> GiveVerdictAsync(
        string owner, string repo, int number, string? verdict, string? body, string? comments, string? commit, CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            throw Refused(GitHubNames.InvalidRepository);
        }

        if (number <= 0)
        {
            throw Refused("The pull request number must be positive.");
        }

        var given = (verdict ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "approve" => ReviewVerdict.Approve,
            "request_changes" => ReviewVerdict.RequestChanges,
            _ => throw Refused(InvalidVerdict),
        };
        var reviewed = (commit ?? string.Empty).Trim();
        if (reviewed.Length < 7)
        {
            throw Refused(NoCommit);
        }

        var requested = CheckReview(comments, body, out var summary);
        var head = await HeadAsync(owner, repo, number, ct).ConfigureAwait(false);
        if (!head.Sha.StartsWith(reviewed, StringComparison.OrdinalIgnoreCase))
        {
            throw Refused(HeadMoved);
        }

        var signer = await SignerLoginAsync(GitHubWriteKind.Verdict, owner, repo, ct).ConfigureAwait(false)
            ?? throw Refused(BotLoginUnknown);
        if (given == ReviewVerdict.Approve && string.Equals(head.AuthorLogin, signer, StringComparison.OrdinalIgnoreCase))
        {
            throw Refused(SelfApproval);
        }

        var (kept, dropped) = await AnchorOnTheDiffAsync(owner, repo, number, requested, summary, ct).ConfigureAwait(false);
        var what = $"{(given == ReviewVerdict.Approve ? "Approved" : "Requested changes on")} {owner}/{repo}#{number.ToString(CultureInfo.InvariantCulture)}";
        if (await GivenAlreadyAsync(owner, repo, number, given, head.Sha, signer, ct).ConfigureAwait(false) is { } earlier)
        {
            var already = new PostedReview(earlier.Id, earlier.HtmlUrl);
            return (already, $"{what} already, at this commit: {already.HtmlUrl}" + Signed(GitHubWriteKind.Verdict, owner, repo), true, signer);
        }

        PostedReview posted;
        try
        {
            posted = await writes.SubmitVerdictAsync(owner, repo, number, given, summary, kept, head.Sha, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: true);
        }

        return (posted, Posted(what, kept, dropped, posted) + Signed(GitHubWriteKind.Verdict, owner, repo), false, signer);
    }

    /// <summary>
    /// The verdict <paramref name="signer"/> already gave with this event on <paramref name="sha"/>, so a verdict whose
    /// answer was lost is never sent twice; null when there is none.
    /// </summary>
    private async Task<PullRequestReview?> GivenAlreadyAsync(
        string owner, string repo, int number, ReviewVerdict verdict, string sha, string signer, CancellationToken ct)
    {
        IReadOnlyList<PullRequestReview>? reviews;
        try
        {
            reviews = await reads.ListReviewsAsync(owner, repo, number, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: false);
        }

        var state = verdict == ReviewVerdict.Approve ? "APPROVED" : "CHANGES_REQUESTED";
        return (reviews ?? throw Refused(NotFound(owner, repo, number)))
            .FirstOrDefault(r => string.Equals(r.AuthorLogin, signer, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.State, state, StringComparison.Ordinal)
                && string.Equals(r.CommitId, sha, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The login that signs a write of <paramref name="kind"/>: the bot's when it signs, else the owner's; null when the bot signs and its login is unknown.</summary>
    private async Task<string?> SignerLoginAsync(GitHubWriteKind kind, string owner, string repo, CancellationToken ct)
    {
        var signer = writes.SignerFor(kind, owner, repo);
        if (signer.ByBot)
        {
            return signer.Login;
        }

        try
        {
            return await reads.GetSignedInLoginAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: false);
        }
    }

    /// <summary>What a write's answer adds when the owner switched it to the bot but the bot is not installed on the repository.</summary>
    private string Signed(GitHubWriteKind kind, string owner, string repo) =>
        writes.SignerFor(kind, owner, repo).BotNotInstalled ? $" Signed as you: the Scout bot is not installed on {owner}/{repo}." : string.Empty;

    /// <summary>Comment on a pull request's conversation; an irreversible write that runs only after the owner approved this comment.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="body">The comment, in Markdown.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>That the comment was posted, with its URL, or why it was refused.</returns>
    [KernelFunction(CommentOnPullRequest)]
    [Description("Ask for a comment on a pull request's conversation, exactly as given. Only after the user asked for it in chat. It is queued for the owner's approval, an Approve card waiting in the chat and in Approvals of Scout Desktop, and sent only after the owner approves it; say that it waits for the owner's approval. Never call it again for the same comment.")]
    public async Task<string> CommentOnPullRequestAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number.")] int number,
        [Description("The comment, in Markdown, up to 65,000 characters.")] string body,
        CancellationToken ct)
    {
        var posted = await CommentAsync(owner, repo, number, body, "pull request", ct).ConfigureAwait(false);
        return $"Commented on pull request {owner}/{repo}#{number.ToString(CultureInfo.InvariantCulture)}: {posted.HtmlUrl}" + Signed(GitHubWriteKind.Comment, owner, repo);
    }

    /// <summary>Comment on an existing issue; an irreversible write that runs only after the owner approved this comment.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Issue number.</param>
    /// <param name="body">The comment, in Markdown.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>That the comment was posted, with its URL, or why it was refused.</returns>
    [KernelFunction(CommentOnIssue)]
    [Description("Ask for a comment on an existing issue, exactly as given. Only after the user asked for it in chat. It is queued for the owner's approval, an Approve card waiting in the chat and in Approvals of Scout Desktop, and sent only after the owner approves it; say that it waits for the owner's approval. Never call it again for the same comment.")]
    public async Task<string> CommentOnIssueAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Issue number.")] int number,
        [Description("The comment, in Markdown, up to 65,000 characters.")] string body,
        CancellationToken ct)
    {
        var posted = await CommentAsync(owner, repo, number, body, "issue", ct).ConfigureAwait(false);
        return $"Commented on issue {owner}/{repo}#{number.ToString(CultureInfo.InvariantCulture)}: {posted.HtmlUrl}" + Signed(GitHubWriteKind.Comment, owner, repo);
    }

    /// <summary>Reply to a review comment on a pull request; an irreversible write that runs only after the owner approved this reply.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="comment_id">The id of the review comment to reply to.</param>
    /// <param name="body">The reply, in Markdown.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>That the reply was posted, with its URL, or why it was refused.</returns>
    [KernelFunction(ReplyToReviewComment)]
    [Description("Ask for a reply to a review comment on a pull request, in its thread, exactly as given. Only after the user asked for it in chat. It is queued for the owner's approval, an Approve card waiting in the chat and in Approvals of Scout Desktop, and sent only after the owner approves it; say that it waits for the owner's approval. Never call it again for the same reply.")]
    public async Task<string> ReplyToReviewCommentAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number.")] int number,
        [Description("The id of the review comment to reply to, a positive number.")] long comment_id,
        [Description("The reply, in Markdown, up to 65,000 characters.")] string body,
        CancellationToken ct)
    {
        var posted = await ReplyAsync(owner, repo, number, comment_id, body, ct).ConfigureAwait(false);
        return $"Replied to review comment {comment_id.ToString(CultureInfo.InvariantCulture)} on {owner}/{repo}#{number.ToString(CultureInfo.InvariantCulture)}: {posted.HtmlUrl}"
            + Signed(GitHubWriteKind.Comment, owner, repo);
    }

    /// <summary>
    /// Post <paramref name="body"/> on the conversation of pull request or issue <paramref name="number"/>, checked first.
    /// </summary>
    internal async Task<PostedComment> CommentAsync(string owner, string repo, int number, string body, string what, CancellationToken ct)
    {
        CheckComment(owner, repo, number, body, what);
        try
        {
            return await writes.CommentOnIssueAsync(owner, repo, number, body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: true);
        }
    }

    /// <summary>Reply <paramref name="body"/> to review comment <paramref name="commentId"/>, checked first.</summary>
    internal async Task<PostedComment> ReplyAsync(string owner, string repo, int number, long commentId, string body, CancellationToken ct)
    {
        CheckComment(owner, repo, number, body, "pull request");
        if (commentId <= 0)
        {
            throw Refused("The review comment id must be positive.");
        }

        try
        {
            return await writes.ReplyToReviewCommentAsync(owner, repo, number, commentId, body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: true);
        }
    }

    private static void CheckComment(string owner, string repo, int number, string? body, string what)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            throw Refused(GitHubNames.InvalidRepository);
        }

        if (number <= 0)
        {
            throw Refused($"The {what} number must be positive.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw Refused(EmptyComment);
        }

        if (body.Length > MaxConversationCommentLength)
        {
            throw Refused(CommentTooLong);
        }
    }

    private static IReadOnlyList<ReviewComment> CheckReview(string? comments, string? body, out string summary)
    {
        var requested = ParseComments(comments) ?? throw Refused(InvalidComments);
        summary = (body ?? string.Empty).Trim();
        if (summary.Length > MaxBodyLength)
        {
            throw Refused($"The review body is over {MaxBodyLength} characters; shorten it.");
        }

        if (requested.Count > MaxComments)
        {
            throw Refused($"The review has more than {MaxComments} comments; keep the ones that matter most.");
        }

        if (requested.Any(c => c.Body.Length > MaxCommentLength))
        {
            throw Refused($"A comment is over {MaxCommentLength} characters; shorten it.");
        }

        return requested;
    }

    private async Task<(IReadOnlyList<ReviewComment> Kept, IReadOnlyList<string> Dropped)> AnchorOnTheDiffAsync(
        string owner, string repo, int number, IReadOnlyList<ReviewComment> requested, string summary, CancellationToken ct)
    {
        IReadOnlyList<ChangedFile>? files;
        try
        {
            files = await reads.ListPullRequestFilesAsync(owner, repo, number, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: false);
        }

        if (files is null)
        {
            throw Refused(NotFound(owner, repo, number));
        }

        var anchored = Anchor(requested, files);
        if (anchored.Kept.Count == 0 && summary.Length == 0)
        {
            throw Refused(NothingToPost);
        }

        return anchored;
    }

    private async Task<PullRequestHead> HeadAsync(string owner, string repo, int number, CancellationToken ct)
    {
        PullRequestHead? head;
        try
        {
            head = await reads.GetPullRequestHeadAsync(owner, repo, number, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: false);
        }

        return head ?? throw Refused(NotFound(owner, repo, number));
    }

    private static string NotFound(string owner, string repo, int number) =>
        $"Pull request #{number.ToString(CultureInfo.InvariantCulture)} not found in {owner}/{repo} or not accessible.";

    private static string Posted(string what, IReadOnlyList<ReviewComment> kept, IReadOnlyList<string> dropped, PostedReview posted)
    {
        var answer = $"{what} with {kept.Count} comment(s): {posted.HtmlUrl}";
        return dropped.Count == 0
            ? answer
            : answer + " Dropped, not on a line of the diff: " + string.Join(", ", dropped) + ".";
    }

    /// <summary>Open an issue on a repository; an irreversible write that runs only after the owner approved this issue.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="title">The issue title.</param>
    /// <param name="body">The issue body, in Markdown.</param>
    /// <param name="labels">Comma-separated label names; empty for none.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>That the issue was opened, with its number and URL, or why it was refused.</returns>
    [KernelFunction(CreateIssue)]
    [Description("Ask for an issue to be opened on a repository. Only after the user asked for the improvement to be filed. The body says what to change, why, how and where (path:line). It is queued for the owner's approval, an Approve card waiting in the chat and in Approvals of Scout Desktop, and sent only after the owner approves it; say that it waits for the owner's approval. Never call it again for the same finding.")]
    public async Task<string> CreateIssueAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("The issue title, under 256 characters.")] string title,
        [Description("The issue body in Markdown: what, why, how and where.")] string body,
        [Description("Comma-separated label names that exist on the repository; empty for none.")] string labels = "",
        CancellationToken ct = default)
    {
        var created = await OpenIssueAsync(owner, repo, title, body, labels, ct).ConfigureAwait(false);
        return $"Opened issue #{created.Number.ToString(CultureInfo.InvariantCulture)} on {owner}/{repo}: {created.HtmlUrl}" + Signed(GitHubWriteKind.Issue, owner, repo);
    }

    /// <summary>Open the issue <paramref name="title"/> on the repository, checked first.</summary>
    internal async Task<CreatedIssue> OpenIssueAsync(
        string owner, string repo, string? title, string? body, string? labels, CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            throw Refused(GitHubNames.InvalidRepository);
        }

        var heading = (title ?? string.Empty).Trim();
        if (heading.Length == 0 || heading.Length > MaxTitleLength)
        {
            throw Refused($"The issue title must be 1 to {MaxTitleLength} characters.");
        }

        var text = (body ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw Refused("The issue needs a body: what to change, why, how and where.");
        }

        if (text.Length > MaxBodyLength)
        {
            throw Refused($"The issue body is over {MaxBodyLength} characters; shorten it.");
        }

        var names = (labels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count > MaxLabels || names.Any(n => n.Length > 50))
        {
            throw Refused($"At most {MaxLabels} labels of up to 50 characters.");
        }

        try
        {
            return await writes.CreateIssueAsync(owner, repo, heading, text, names, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: true);
        }
    }

    /// <summary>Start the audit of a whole repository as a job with saved progress; a write into Scout's data folder.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>The audit's plan, or why it did not start.</returns>
    [KernelFunction(StartAudit)]
    [Description("Start an audit of a whole repository as a job that runs in the background with saved progress: Scout takes a snapshot, plans the areas and reports the findings in this chat as each area is done. Only when the user asks for an audit of a whole repository or project. Answers at once with the plan.")]
    public Task<string> StartRepositoryAuditAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        CancellationToken ct) =>
        Audit(owner, repo, a => a.StartAsync(owner, repo, ct));

    /// <summary>Pause the repository's running audit; an area in progress finishes first, and a start resumes it.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the pause.</param>
    /// <returns>That the audit pauses, or why not.</returns>
    [KernelFunction(PauseAudit)]
    [Description("Pause the running audit of a repository; an area in progress finishes first, the findings so far stay, and asking to continue the audit resumes it from the first area not done. Only when the user asks to pause it.")]
    public Task<string> PauseRepositoryAuditAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        CancellationToken ct) =>
        Audit(owner, repo, a => a.PauseAsync(owner, repo, ct));

    /// <summary>Stop the repository's running or paused audit; an area in progress finishes first.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the stop.</param>
    /// <returns>That the audit stops, or why not.</returns>
    [KernelFunction(StopAudit)]
    [Description("Stop the running or paused audit of a repository; an area in progress finishes first, and the findings so far stay. Only when the user asks to stop it.")]
    public Task<string> StopRepositoryAuditAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        CancellationToken ct) =>
        Audit(owner, repo, a => a.StopAsync(owner, repo, ct));

    /// <summary>How the repository's latest audit is going.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The audit's progress, or that there is none.</returns>
    [KernelFunction(AuditStatus)]
    [Description("Say how the latest audit of a repository is going: the areas done, the one running, the findings so far.")]
    public Task<string> GetRepositoryAuditStatusAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        CancellationToken ct) =>
        Audit(owner, repo, a => a.StatusAsync(owner, repo, ct));

    private Task<string> Audit(string owner, string repo, Func<IRepositoryAudits, Task<string>> call) =>
        !GitHubNames.IsValidRepository(owner, repo) ? Task.FromResult(GitHubNames.InvalidRepository)
        : audits is null ? Task.FromResult(NoAudits)
        : call(audits);

    /// <summary>Take a snapshot of a repository at a commit, under Scout's data folder, so the reads and the Knowledge index work on it locally.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="reference">Branch, tag or commit; empty for the default branch.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>What was taken and where it is searchable, or why it was refused.</returns>
    [KernelFunction(SnapshotRepository)]
    [Description("Download a repository at a branch, tag or commit (empty for the default branch) into Scout's data folder, so get_file_contents, list_repository_files and search_code read every file locally, at that commit, with no API limits, and search_project and find_symbol can search it as the project named owner/repo. Take one before reviewing a large pull request (at its head branch) or auditing a whole repository. Refused over the size cap (200 MB unless configured).")]
    public async Task<string> SnapshotRepositoryAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Branch, tag or commit to snapshot; empty for the default branch.")] string reference = "",
        CancellationToken ct = default)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            throw Refused(GitHubNames.InvalidRepository);
        }

        if (!GitHubNames.IsValidReference(reference))
        {
            throw Refused(GitHubNames.InvalidReference);
        }

        RepositoryCommit? commit;
        try
        {
            commit = await reads.ResolveCommitAsync(owner, repo, reference, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: false);
        }

        if (commit is null)
        {
            throw Refused($"{owner}/{repo}{(reference.Length == 0 ? string.Empty : " at " + reference)} not found or not accessible.");
        }

        var name = $"{owner}/{repo}";
        var (snapshot, already) = await SnapshotAtAsync(owner, repo, commit, ct).ConfigureAwait(false);
        projects.Register(name, snapshot.Root);
        return Taken(snapshot, name, already);
    }

    /// <summary>The snapshot at <paramref name="commit"/>, the one already kept or one downloaded now, and whether it was already kept.</summary>
    private async Task<(RepositorySnapshot Snapshot, bool Already)> SnapshotAtAsync(string owner, string repo, RepositoryCommit commit, CancellationToken ct)
    {
        if (snapshots.FindBySha(owner, repo, commit.Sha) is { } existing)
        {
            if (existing.Reference != commit.Reference || existing.IsDefaultBranch != commit.IsDefaultBranch)
            {
                existing = await RepositorySnapshotStore.RelabelAsync(existing, commit, ct).ConfigureAwait(false);
            }

            await RecordStackAsync(existing, ct).ConfigureAwait(false);
            return (existing, true);
        }

        RepositorySnapshot? taken = null;
        try
        {
            var found = await reads.DownloadTarballAsync(owner, repo, commit.Sha, async (stream, token) =>
            {
                taken = await snapshots.ExtractAsync(owner, repo, commit, stream, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (!found || taken is null)
            {
                throw Refused($"{owner}/{repo} at {commit.Reference} has no tarball GitHub can serve.");
            }
        }
        catch (SnapshotRefusedException ex)
        {
            throw Refused(ex.Message);
        }
        catch (Exception ex) when ((IsHandledFailure(ex) || ex is IOException) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: false);
        }

        await RecordStackAsync(taken, ct).ConfigureAwait(false);
        return (taken, false);
    }

    /// <summary>
    /// Keeps the stack the snapshot would run its tests on beside the snapshots, for the Docker card; a detection or a
    /// write that fails is logged and changes nothing else.
    /// </summary>
    private async Task RecordStackAsync(RepositorySnapshot snapshot, CancellationToken ct)
    {
        if (stacks is null)
        {
            return;
        }

        try
        {
            await snapshots.WriteStackAsync(snapshot, stacks.Detect(snapshot.Root), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            (logger ?? NullLogger<GitHubActionsPlugin>.Instance).LogWarning(
                "The stack of {Repository} at {Commit} was not recorded; the snapshot is unchanged: {Reason}", $"{snapshot.Owner}/{snapshot.Repo}", snapshot.ShortSha, ex.Message);
        }
    }

    private static string Taken(RepositorySnapshot snapshot, string project, bool already) =>
        $"{(already ? "Already have a snapshot of" : "Took a snapshot of")} {snapshot.Label}: {snapshot.FileCount.ToString(CultureInfo.InvariantCulture)} files, {(snapshot.Bytes / 1024).ToString(CultureInfo.InvariantCulture)} KB. "
        + $"get_file_contents, list_repository_files and search_code now read it locally at that commit; search_project and find_symbol search it as the project \"{project}\" (indexed on first use).";

    /// <summary>The conventions profile of a repository, as Scout discovered it or the owner corrected it.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The profile with its date, or that none exists yet.</returns>
    [KernelFunction(GetConventions)]
    [Description("Read the conventions profile Scout keeps for a repository: naming, layers, error handling, tests, dependency injection, logging, as its own files define them, with the date it was written. Read it before judging the repository's code; when there is none, discover the conventions and save them with remember_project_conventions.")]
    public async Task<string> GetProjectConventionsAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        try
        {
            var profile = await profiles.FindAsync(owner, repo, ct).ConfigureAwait(false);
            return profile is null
                ? $"No conventions profile for {owner}/{repo} yet. Discover the conventions from the project's own files and save them with {RememberConventions}."
                : $"Conventions of {owner}/{repo}, written {profile.UpdatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:\n{profile.Conventions}";
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            return GitHubPlugin.ErrorLineFor(ex);
        }
    }

    /// <summary>Keep the conventions profile of a repository under Scout's data folder; a write under the GitHub grant.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="conventions">The profile in Markdown.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>That the profile was saved, or why it was refused.</returns>
    [KernelFunction(RememberConventions)]
    [Description("Save or replace the conventions profile of a repository under Scout's data folder, so later audits judge the code against the house rules. Write it from the project's own files (manifests, lint and format configuration, CI, README, CONTRIBUTING, architecture notes, a sample of files per layer) and from what the user corrects in chat; never from assumptions. Markdown, 40 to 20,000 characters, with a line per rule and the file that shows it.")]
    public async Task<string> RememberProjectConventionsAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("The profile in Markdown: one line per convention, each naming the file that shows it.")] string conventions,
        CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        var text = (conventions ?? string.Empty).Trim();
        if (text.Length < MinConventionsLength || text.Length > MaxConventionsLength)
        {
            return $"The conventions profile must be {MinConventionsLength} to {MaxConventionsLength} characters.";
        }

        try
        {
            await profiles.SaveAsync(new GitHubProjectProfile(owner, repo, text, time.GetUtcNow(), ChatOrigin.Current), ct).ConfigureAwait(false);
            return $"Saved the conventions profile of {owner}/{repo} ({text.Length} characters). Later audits read it with {GetConventions}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            return GitHubPlugin.ErrorLineFor(ex);
        }
    }

    internal static IReadOnlyList<ReviewComment>? ParseComments(string? comments)
    {
        if (string.IsNullOrWhiteSpace(comments))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<CommentDto>>(comments, Json);
            if (parsed is null || parsed.Any(c => string.IsNullOrWhiteSpace(c.Path) || c.Line <= 0 || string.IsNullOrWhiteSpace(c.Body)))
            {
                return null;
            }

            return parsed.Select(c => new ReviewComment(c.Path!.Trim(), c.Line, c.Body!.Trim())).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Keep the comments whose path and line sit on the right side of the diff; name the rest as <c>path:line</c>.</summary>
    internal static (IReadOnlyList<ReviewComment> Kept, IReadOnlyList<string> Dropped) Anchor(
        IReadOnlyList<ReviewComment> requested, IReadOnlyList<ChangedFile> files)
    {
        var lines = files.ToDictionary(f => f.Path, f => DiffLines.RightSideLines(f.Patch), StringComparer.Ordinal);
        var kept = new List<ReviewComment>();
        var dropped = new List<string>();
        foreach (var comment in requested)
        {
            if (lines.TryGetValue(comment.Path, out var anchors) && anchors.Contains(comment.Line))
            {
                kept.Add(comment);
            }
            else
            {
                dropped.Add($"{comment.Path}:{comment.Line.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        return (kept, dropped);
    }

    internal const string MayAlreadyBePosted =
        " GitHub did not answer after the request was sent, so it may already be there: check the repository before asking again.";

    /// <summary>A write Scout refused before sending anything.</summary>
    private static WriteFailedException Refused(string reason) => new(reason, "refused");

    /// <summary>
    /// A write GitHub refused, or one the network lost. After the request left (<paramref name="posted"/>), a lost
    /// answer may hide an accepted write, and the sentence says so.
    /// </summary>
    private static WriteFailedException Failed(Exception ex, bool posted) => ex switch
    {
        GitHubApiException api => new WriteFailedException(GitHubPlugin.ErrorLineFor(api), api.StatusCode is { } code ? $"github {((int)code).ToString(CultureInfo.InvariantCulture)}" : "github"),
        _ => new WriteFailedException(GitHubPlugin.ErrorLineFor(ex) + (posted ? MayAlreadyBePosted : string.Empty), ex.GetType().Name),
    };

    private static bool IsHandledFailure(Exception ex)
        => ex is GitHubApiException or HttpRequestException or OperationCanceledException or IOException;

    private sealed record CommentDto(string? Path, int Line, string? Body);
}
