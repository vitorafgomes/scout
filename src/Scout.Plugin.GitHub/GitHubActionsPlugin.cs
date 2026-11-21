using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Review;

namespace Scout.Plugin.GitHub;

/// <summary>
/// The writes the model may ask for around GitHub, each declared on its own row of the <c>github</c> feature and run
/// only under the owner's grant: <c>submit_pull_request_review</c> posts a commenting review on a pull request,
/// <c>create_issue</c> opens an issue, <c>comment_on_pull_request</c> and <c>comment_on_issue</c> comment on a
/// conversation, <c>reply_to_review_comment</c> answers a review comment and <c>give_pull_request_verdict</c> gives the
/// verdict the review offers, refusing an approval the review does not offer, and Scout does not offer one on its own: each of these is an irreversible write the gate queues, with its arguments, for
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
    RunningTests? running = null,
    IPullRequestReviews? reviews = null)
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
    internal const string ChecksNotPassed = "Scout approves only when every check of the commit passed:";
    internal const string Conflicts = "The pull request has conflicts with its base branch; Scout approves only a pull request that can be merged.";
    internal const string MergeabilityUnknown = "GitHub has not said whether the pull request can be merged. If it is still open, approve again in a moment.";
    internal const string ChecksNotAllRead = "The commit has more checks than Scout could read; it approves only when it has read every one.";
    internal const int MaxChecksNamed = 3;
    internal const int MaxCheckNameChars = 60;
    internal const string SelfApproval = "GitHub does not let the author approve their own pull request; request changes or comment instead.";

    internal const int MaxBodyLength = 10_000;
    internal const int MaxTitleLength = 256;
    internal const int MaxLabels = 10;

    internal const string NoteTooLong = "The review is over 60,000 characters; keep fewer findings.";
    internal const string NoteCommentTooLong = "A comment is over 10,000 characters; shorten its text on the card.";

    /// <summary>Post a commenting review on a pull request; an irreversible write that runs only after the owner approved this review.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="findings_json">The findings as the JSON array <c>present_review_findings</c> takes.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>That the review was posted, with its URL and the comments dropped for pointing outside the diff, or why it was refused.</returns>
    [KernelFunction(SubmitReview)]
    [Description("Ask for a code review to be posted on a pull request as a commenting review (never an approval or a request for changes): pass the findings, and Scout writes the note and one comment on its line for each finding; never pass a body. Only after the user asked in chat for the review to be posted; the review card's Post button does not go through chat. It is queued for the owner's approval, an Approve card waiting in the chat and in Approvals of Scout Desktop, and sent only after the owner approves it; say that it waits for the owner's approval. Never call it again for the same review.")]
    public async Task<string> SubmitPullRequestReviewAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number.")] int number,
        [Description("""The findings, the same JSON array present_review_findings takes: [{"id":"lock","severity":"block","title":"Move has no lock","path":"src/Cart.cs","line":12,"why":"Two moves lose an item.","fix":"Take the lock."}], with "context" and "rule" when known; [] when there are none. Scout writes the note and the comments from them; never pass a body.""")] string findings_json,
        CancellationToken ct) =>
        (await PostReviewAsync(owner, repo, number, findings_json, null, null, ct).ConfigureAwait(false)).Answer;

    /// <summary>
    /// Post the commenting review on the pull request, its note and comments composed by code from
    /// <paramref name="findings"/> at <paramref name="commit"/> (the head when none is given), refused before anything is
    /// sent when a value is wrong.
    /// </summary>
    internal async Task<(PostedReview Posted, string Answer)> PostReviewAsync(
        string owner, string repo, int number, string? findings, string? commit, NoteExtras? extras, CancellationToken ct)
    {
        var (body, kept, dropped, _, _) = await PrepareReviewAsync(owner, repo, number, findings, commit, extras, ct).ConfigureAwait(false);

        PostedReview posted;
        try
        {
            posted = await writes.SubmitReviewAsync(owner, repo, number, body, kept, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: true);
        }

        return (posted, Posted($"Posted a review on {owner}/{repo}#{number.ToString(CultureInfo.InvariantCulture)}", kept, dropped, posted)
            + Signed(GitHubWriteKind.Review, owner, repo));
    }

    /// <summary>A commenting review's note and comments, composed with its reads and refused as the post refuses; nothing is sent.</summary>
    private async Task<(string Body, IReadOnlyList<ReviewComment> Kept, IReadOnlyList<string> Dropped, IReadOnlyList<Finding> Findings, IReadOnlyList<PullRequestCheck>? Checks)> PrepareReviewAsync(
        string owner, string repo, int number, string? findings, string? commit, NoteExtras? extras, CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            throw Refused(GitHubNames.InvalidRepository);
        }

        if (number <= 0)
        {
            throw Refused("The pull request number must be positive.");
        }

        var requested = CheckFindings(findings);
        var head = await HeadAsync(owner, repo, number, ct).ConfigureAwait(false);
        string? signer;
        try
        {
            signer = await SignerLoginAsync(GitHubWriteKind.Review, owner, repo, ct).ConfigureAwait(false);
        }
        catch (WriteFailedException)
        {
            signer = null;
        }

        var reviewed = string.IsNullOrWhiteSpace(commit) ? head.Sha : commit.Trim();
        if (!head.Sha.StartsWith(reviewed, StringComparison.OrdinalIgnoreCase))
        {
            throw Refused(HeadMoved);
        }

        var (body, kept, dropped, checks) = await ComposeAsync(
            owner, repo, number, ReviewNote.Commenting, requested, head, reviewed, extras, null, signer, writes.SignerFor(GitHubWriteKind.Review, owner, repo).ByBot, ct).ConfigureAwait(false);
        return (body, kept, dropped, requested, checks);
    }

    /// <summary>
    /// Give the verdict the review offers on a pull request: an approval is refused unless the review offers it, and Scout
    /// does not offer one on its own; an irreversible write that runs only after the owner approved this action.
    /// </summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="verdict"><c>approve</c> or <c>request_changes</c>.</param>
    /// <param name="findings_json">The findings as the JSON array <c>present_review_findings</c> takes.</param>
    /// <param name="commit">The head commit the review read.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>That the verdict was given, with its URL and the comments dropped for pointing outside the diff, or why it was refused.</returns>
    [KernelFunction(GiveVerdict)]
    [Description("Give the verdict the review offers on a pull request, pinned to the head commit the review read: request changes with the findings that need them; pass the findings, and Scout writes the note and one comment on its line for each finding; never pass a body. Scout does not approve on its own: an approval is refused unless the review offers it, so a person approves on GitHub. Call it only when the user asks for that verdict in chat, never on your own judgement; the review card's action does not go through chat. The verdict is queued for the owner's approval, an Approve card waiting in the chat and in Approvals of Scout Desktop, and sent after the owner approves it; it is refused when the pull request moved past the commit reviewed. GitHub does not let the author approve their own pull request.")]
    public async Task<string> GivePullRequestVerdictAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number.")] int number,
        [Description("approve or request_changes.")] string verdict,
        [Description("""The findings, the same JSON array present_review_findings takes: [{"id":"lock","severity":"block","title":"Move has no lock","path":"src/Cart.cs","line":12,"why":"Two moves lose an item.","fix":"Take the lock."}], with "context" and "rule" when known; [] when there are none. Scout writes the note and the comments from them; never pass a body.""")] string findings_json,
        [Description("The head commit the review read, e.g. ec7f951.")] string commit,
        CancellationToken ct) =>
        (await GiveVerdictAsync(owner, repo, number, verdict, findings_json, commit, null, ct).ConfigureAwait(false)).Answer;

    /// <summary>
    /// Give the verdict on the pull request, refused before anything is sent when a value is wrong, the pull request moved
    /// past <paramref name="commit"/>, an approval meets a pull request that conflicts with its base branch or whose
    /// mergeability GitHub has not computed, or the identity that signs it opened the pull request.
    /// </summary>
    internal async Task<(PostedReview Posted, string Answer, bool Already, string? Signer)> GiveVerdictAsync(
        string owner, string repo, int number, string? verdict, string? findings, string? commit, NoteExtras? extras, CancellationToken ct)
    {
        var prepared = await PrepareVerdictAsync(owner, repo, number, verdict, findings, commit, extras, ct).ConfigureAwait(false);
        if (prepared.Given == ReviewVerdict.RequestChanges && prepared.Findings.Count == 0)
        {
            throw Refused(NothingToChange);
        }

        var (given, signer, body, kept, dropped) = (prepared.Given, prepared.Signer, prepared.Body, prepared.Kept, prepared.Dropped);
        var what = $"{(given == ReviewVerdict.Approve ? "Approved" : "Requested changes on")} {owner}/{repo}#{number.ToString(CultureInfo.InvariantCulture)}";
        if (await GivenAlreadyAsync(owner, repo, number, given, prepared.Head.Sha, signer, ReviewNote.Digest(prepared.Note, prepared.Findings, extras?.FilesRead, extras?.FilesChanged, extras?.Notes), ct).ConfigureAwait(false) is { } earlier)
        {
            var already = new PostedReview(earlier.Id, earlier.HtmlUrl);
            return (already, $"{what} already, at this commit: {already.HtmlUrl}" + Signed(GitHubWriteKind.Verdict, owner, repo), true, signer);
        }

        PostedReview posted;
        try
        {
            posted = await writes.SubmitVerdictAsync(owner, repo, number, given, body, kept, prepared.Head.Sha, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: true);
        }

        return (posted, Posted(what, kept, dropped, posted) + Signed(GitHubWriteKind.Verdict, owner, repo), false, signer);
    }

    /// <summary>A verdict composed with its reads, and refused as the send refuses it.</summary>
    private sealed record PreparedVerdict(
        ReviewVerdict Given, string Note, IReadOnlyList<Finding> Findings, PullRequestHead Head, string Signer,
        string Body, IReadOnlyList<ReviewComment> Kept, IReadOnlyList<string> Dropped, IReadOnlyList<PullRequestCheck>? Checks);

    /// <summary>
    /// The checks and reads of a verdict and its note and comments, composed; refused before anything is sent when a value
    /// is wrong or the pull request does not allow it. Nothing is sent.
    /// </summary>
    private async Task<PreparedVerdict> PrepareVerdictAsync(
        string owner, string repo, int number, string? verdict, string? findings, string? commit, NoteExtras? extras, CancellationToken ct)
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

        var requested = CheckFindings(findings);
        var head = await HeadAsync(owner, repo, number, ct).ConfigureAwait(false);
        if (!head.Sha.StartsWith(reviewed, StringComparison.OrdinalIgnoreCase))
        {
            throw Refused(HeadMoved);
        }

        if (given == ReviewVerdict.Approve && (head.Mergeable == false || head.MergeableState == "dirty"))
        {
            throw Refused(Conflicts);
        }

        if (given == ReviewVerdict.Approve && head.Mergeable is null)
        {
            throw Refused(MergeabilityUnknown);
        }

        var signer = await SignerLoginAsync(GitHubWriteKind.Verdict, owner, repo, ct).ConfigureAwait(false)
            ?? throw Refused(BotLoginUnknown);
        if (given == ReviewVerdict.Approve && string.Equals(head.AuthorLogin, signer, StringComparison.OrdinalIgnoreCase))
        {
            throw Refused(SelfApproval);
        }

        var checks = given == ReviewVerdict.Approve ? await RefuseUnlessEveryCheckPassedAsync(owner, repo, head.Sha, ct).ConfigureAwait(false) : null;
        if (given == ReviewVerdict.Approve && OfferFor(requested, extras, testRuns?.Find(owner, repo, head.Sha), checks) is { Verdict: not ReviewOffers.Approve } offer)
        {
            throw Refused($"{NotOffered} {offer.Reason}");
        }

        var note = given == ReviewVerdict.Approve ? ReviewNote.Approve : ReviewNote.RequestChanges;
        var (body, kept, dropped, read) = await ComposeAsync(
            owner, repo, number, note, requested, head, head.Sha, extras, checks, signer, writes.SignerFor(GitHubWriteKind.Verdict, owner, repo).ByBot, ct).ConfigureAwait(false);
        return new PreparedVerdict(given, note, requested, head, signer, body, kept, dropped, read);
    }

    /// <summary>The refusal of a request for changes sent with no finding: there is nothing to change.</summary>
    internal const string NothingToChange = "Scout requests changes only with the findings that need them; with none chosen, post a comment instead.";

    /// <summary>The start of the refusal of an approval the review does not offer; the offer's reason follows it.</summary>
    internal const string NotOffered = "Scout approves only when the review offers approval.";

    /// <summary>The tests as the decision takes them when no run is kept for the commit: no test ran here.</summary>
    private static readonly TestRunFacts NoRunKept = new(false, "No test run is kept for this commit.", null, null, [], false);

    /// <summary>
    /// What the review offers (<see cref="ReviewOffers.Decide"/>) for <paramref name="findings"/>, the files read and changed
    /// the card sent, the test run kept for the commit and its checks; a finding whose title starts with
    /// <see cref="Contestation.UnconfirmedPrefix"/> is unconfirmed. Files not counted are taken as all read, and the
    /// author, conflicts and mergeability are refused beside it with their own sentences. With no test run kept for the
    /// commit the decision is told that no test ran here, so its reason never says the tests passed.
    /// </summary>
    internal static Offer OfferFor(IReadOnlyList<Finding> findings, NoteExtras? extras, ReviewTests? tests, IReadOnlyList<PullRequestCheck>? checks) =>
        ReviewOffers.Decide(new OfferInput(
            [.. findings.Select(f => new OfferFinding(f.Severity, f.Title.StartsWith(Contestation.UnconfirmedPrefix, StringComparison.Ordinal)))],
            extras?.FilesRead ?? 0,
            extras?.FilesChanged ?? extras?.FilesRead ?? 0,
            TestsOf(tests),
            PullRequestChecks.Offer(checks)));

    /// <summary>A kept test run as the decision reads it; with none kept, a run that did not happen.</summary>
    internal static TestRunFacts TestsOf(ReviewTests? tests) =>
        tests is null ? NoRunKept : new TestRunFacts(tests.Ran, tests.Reason, tests.Command, tests.ExitCode, tests.Unrunnable is null ? [] : [new TestProjectFacts(string.Empty, 0, 1, 0, true)], false);

    /// <summary>
    /// The note and comments a verdict, or for <c>comment</c> a review that only comments, would post now: the same checks,
    /// reads and composing as the send, refused as the send would be, and nothing sent.
    /// </summary>
    internal async Task<(string Body, IReadOnlyList<(string Id, ReviewComment Comment)> Comments, IReadOnlyList<string> Dropped, Offer Offer)> PreviewAsync(
        string owner, string repo, int number, string? verdict, string? findings, string? commit, NoteExtras? extras, CancellationToken ct)
    {
        string body;
        IReadOnlyList<ReviewComment> kept;
        IReadOnlyList<string> dropped;
        IReadOnlyList<Finding> requested;
        IReadOnlyList<PullRequestCheck>? checks;
        if (string.Equals(verdict?.Trim(), ReviewNote.Commenting, StringComparison.OrdinalIgnoreCase))
        {
            (body, kept, dropped, requested, checks) = await PrepareReviewAsync(owner, repo, number, findings, commit, extras, ct).ConfigureAwait(false);
        }
        else
        {
            var prepared = await PrepareVerdictAsync(owner, repo, number, verdict, findings, commit, extras, ct).ConfigureAwait(false);
            (body, kept, dropped, requested, checks) = (prepared.Body, prepared.Kept, prepared.Dropped, prepared.Findings, prepared.Checks);
        }

        var comments = requested
            .Select(f => (f.Id, Comment: new ReviewComment(f.Path, f.Line, ReviewNote.Comment(f))))
            .Where(c => kept.Contains(c.Comment))
            .ToList();
        var offer = OfferFor(requested, extras, testRuns?.Find(owner, repo, (commit ?? string.Empty).Trim()), checks);
        return (body, comments, dropped, offer);
    }

    /// <summary>
    /// Refuse an approval unless every check run and commit status at <paramref name="sha"/> passed: a check still
    /// running, one that failed, was cancelled or timed out, and a check Scout could not read all refuse it: more check
    /// runs than the page read, or a full page of statuses, which may hide more. A skipped or neutral check passes, and a
    /// commit with no check at all may be approved. A read that fails sends nothing. A name in the answer is one line of
    /// at most <see cref="MaxCheckNameChars"/> characters. Returns the checks it read, for the note.
    /// </summary>
    private async Task<IReadOnlyList<PullRequestCheck>> RefuseUnlessEveryCheckPassedAsync(string owner, string repo, string sha, CancellationToken ct)
    {
        IReadOnlyList<PullRequestCheck>? read;
        try
        {
            read = await ReadChecksAsync(owner, repo, sha, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: false);
        }

        var checks = read?.ToList() ?? throw Refused(ChecksNotAllRead);
        var running = checks.Where(c => c.Status != "completed").Select(c => c.Name).ToList();
        var failed = checks.Where(c => c.Status == "completed" && c.Conclusion is not ("success" or "neutral" or "skipped")).Select(c => c.Name).ToList();
        if (running.Count == 0 && failed.Count == 0)
        {
            return checks;
        }

        var parts = new List<string>();
        if (running.Count > 0)
        {
            parts.Add($"{running.Count.ToString(CultureInfo.InvariantCulture)} still running ({Named(running)})");
        }

        if (failed.Count > 0)
        {
            parts.Add($"{failed.Count.ToString(CultureInfo.InvariantCulture)} did not pass ({Named(failed)})");
        }

        throw Refused($"{ChecksNotPassed} {string.Join(", ", parts)}.");
    }

    private static string Named(List<string> names) =>
        string.Join(", ", names.Take(MaxChecksNamed).Select(OneLine)) + (names.Count > MaxChecksNamed ? ", ..." : string.Empty);

    private static string OneLine(string name)
    {
        var flat = new string(name.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return flat.Length <= MaxCheckNameChars ? flat : flat[..MaxCheckNameChars] + "...";
    }

    /// <summary>
    /// The verdict <paramref name="signer"/> already gave with this event on <paramref name="sha"/> whose note carries the
    /// marker of <paramref name="digest"/>, so a verdict whose answer was lost is never sent twice while one with other
    /// findings is; null when there is none.
    /// </summary>
    private async Task<PullRequestReview?> GivenAlreadyAsync(
        string owner, string repo, int number, ReviewVerdict verdict, string sha, string signer, string digest, CancellationToken ct)
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
                && string.Equals(r.CommitId, sha, StringComparison.OrdinalIgnoreCase)
                && r.Body.Contains(ReviewNote.Marker(digest), StringComparison.Ordinal));
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

    /// <summary>The findings of a review or a verdict, as the card's parser reads them; refused with its sentence when one is wrong.</summary>
    private static IReadOnlyList<Finding> CheckFindings(string? findings)
    {
        var (parsed, problem) = ReviewFindings.ParseFindings(findings, ReviewFindings.InvalidReviewFindings);
        return parsed ?? throw Refused(problem!);
    }

    /// <summary>
    /// The one path of a review and a verdict: each finding's comment anchored on the diff, the checks of the commit read
    /// unless <paramref name="checksRead"/> holds them (a failed or partial read leaves them out), the kept test run, and
    /// the note composed by <see cref="ReviewNote"/>; refused before anything is sent when a comment or the note is too long.
    /// </summary>
    private async Task<(string Body, IReadOnlyList<ReviewComment> Kept, IReadOnlyList<string> Dropped, IReadOnlyList<PullRequestCheck>? Checks)> ComposeAsync(
        string owner, string repo, int number, string verdict, IReadOnlyList<Finding> findings, PullRequestHead head, string commit,
        NoteExtras? extras, IReadOnlyList<PullRequestCheck>? checksRead, string? signer, bool signedByBot, CancellationToken ct)
    {
        var requested = findings.Select(f => new ReviewComment(f.Path, f.Line, ReviewNote.Comment(f))).ToList();
        if (requested.Any(c => c.Body.Length > ReviewNote.MaxCommentLength))
        {
            throw Refused(NoteCommentTooLong);
        }

        var (kept, dropped) = await AnchorOnTheDiffAsync(owner, repo, number, requested, ct).ConfigureAwait(false);
        var current = head.Sha.StartsWith(commit, StringComparison.OrdinalIgnoreCase);
        var checks = checksRead ?? (current ? await ChecksOrNoneAsync(owner, repo, head.Sha, ct).ConfigureAwait(false) : null);
        var body = ReviewNote.Body(new ReviewNoteInput(
            owner, repo, number, verdict, current ? head.Sha : commit, findings, checks, testRuns?.Find(owner, repo, head.Sha),
            extras?.FilesRead, extras?.FilesChanged, extras?.Notes, signer, signedByBot, dropped.Count == 0));
        return (body ?? throw Refused(NoteTooLong), kept, dropped, checks);
    }

    /// <summary>The check runs and statuses of <paramref name="sha"/>, read together; null when GitHub has more than Scout read. A failed read throws.</summary>
    private Task<IReadOnlyList<PullRequestCheck>?> ReadChecksAsync(string owner, string repo, string sha, CancellationToken ct) =>
        PullRequestChecks.ReadAsync(reads, owner, repo, sha, ct);

    /// <summary>The check runs and statuses of <paramref name="sha"/>, or null when a read fails or GitHub has more than Scout read.</summary>
    private async Task<IReadOnlyList<PullRequestCheck>?> ChecksOrNoneAsync(string owner, string repo, string sha, CancellationToken ct)
    {
        try
        {
            return await ReadChecksAsync(owner, repo, sha, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<(IReadOnlyList<ReviewComment> Kept, IReadOnlyList<string> Dropped)> AnchorOnTheDiffAsync(
        string owner, string repo, int number, IReadOnlyList<ReviewComment> requested, CancellationToken ct)
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

        return Anchor(requested, files);
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
                "The stack of {Repository} at {Commit} was not recorded; the snapshot is unchanged ({ExceptionType}).", $"{snapshot.Owner}/{snapshot.Repo}", snapshot.ShortSha, ex.GetType().Name);
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

    /// <summary>Keep the comments whose path and line sit on the right side of the diff; name the rest as <c>path:line</c>.</summary>
    internal static (IReadOnlyList<ReviewComment> Kept, IReadOnlyList<string> Dropped) Anchor(
        IReadOnlyList<ReviewComment> requested, IReadOnlyList<ChangedFile> files)
    {
        var named = requested.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var lines = files.Where(f => named.Contains(f.Path)).ToDictionary(f => f.Path, f => DiffLines.RightSideLines(f.Patch), StringComparer.Ordinal);
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

}
