using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// The writes the model may ask for around GitHub. <c>submit_pull_request_review</c> and <c>create_issue</c> are each a
/// draft Scout keeps and an action the owner approves: neither reaches GitHub; each is declared as a write into Scout's
/// data folder, so the write gate refuses it before anything is saved while the owner has not allowed GitHub writes;
/// under the grant it checks the request, saves the draft, and asks the control plane for the irreversible action,
/// which waits on the Review screen, and the <see cref="GitHubActionExecutor"/> posts it after the owner approves.
/// <c>remember_project_conventions</c> is a reversible write into the same folder: the conventions profile of a
/// repository an audit judges the code against, read back with <c>get_project_conventions</c>. The model is told an
/// action waits, never that it ran.
/// </summary>
internal sealed class GitHubActionsPlugin(
    IGitHubClient reads,
    IWriteGate gate,
    GitHubDraftStore drafts,
    GitHubProfileStore profiles,
    TimeProvider time)
{
    internal const string PluginName = "GitHubActions";
    internal const string SubmitReview = "submit_pull_request_review";
    internal const string CreateIssue = "create_issue";
    internal const string RememberConventions = "remember_project_conventions";
    internal const string GetConventions = "get_project_conventions";

    internal const int MaxConventionsLength = 20_000;
    internal const int MinConventionsLength = 40;

    /// <summary>The catalog id of the GitHub feature.</summary>
    internal const string FeatureId = "github";

    /// <summary>The catalog row the drafts are saved under.</summary>
    internal const string DataFolderScope = "Scout's data folder";

    /// <summary>The catalog row a posted review performs.</summary>
    internal const string ReviewScope = "Pull request reviews";

    /// <summary>The catalog row an opened issue performs.</summary>
    internal const string IssueScope = "New issues";

    internal const int MaxComments = 50;
    internal const int MaxBodyLength = 10_000;
    internal const int MaxCommentLength = 2_000;
    internal const int MaxTitleLength = 256;
    internal const int MaxLabels = 10;
    private const int SummaryExcerpt = 200;

    internal const string NothingToPost = "Nothing to post: the review has no body and no comment on a line of the diff.";
    internal const string InvalidComments =
        "The comments must be a JSON array of objects with \"path\", \"line\" (a positive integer) and \"body\".";
    internal const string ControlPlaneUnreachable =
        "Scout could not reach its control plane, so nothing was queued; nothing was posted.";

    internal const string UnexpectedlyAllowed =
        "The control plane allowed this write without the owner's approval, which Scout does not do for a review or an issue; nothing was posted.";

    internal static string Queued(string actionId, string what) =>
        $"Queued for the owner's approval on the Review screen of Scout Desktop (action {actionId}): {what}. It is not posted yet; the chat hears when it is.";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Queue a review with comments on a pull request for the owner's approval.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="body">The review's summary text; may be empty when there are comments.</param>
    /// <param name="comments">JSON array of comments: path, line (in the file after the change), body.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>That the review waits for the owner, with the comments dropped for pointing outside the diff, or why it was refused.</returns>
    [KernelFunction(SubmitReview)]
    [Description("Queue a code review for the owner's approval: a summary and comments anchored to lines of the pull request's diff, posted on the pull request as comments (never an approval) once the owner approves it on the Review screen of Scout Desktop. Only after the user asked for the review to be posted. A comment whose path and line are not in the diff is dropped and named in the answer.")]
    public async Task<string> SubmitPullRequestReviewAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number.")] int number,
        [Description("The review's summary, in Markdown; empty when the comments say it all.")] string body,
        [Description("""JSON array of comments, e.g. [{"path":"src/Checkout.cs","line":88,"body":"`total` is summed before the discounts apply."}]; line is the line number in the file after the change, as the diff shows it.""")] string comments,
        CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        if (number <= 0)
        {
            return "The pull request number must be positive.";
        }

        var requested = ParseComments(comments);
        if (requested is null)
        {
            return InvalidComments;
        }

        var summary = (body ?? string.Empty).Trim();
        if (summary.Length > MaxBodyLength)
        {
            return $"The review body is over {MaxBodyLength} characters; shorten it.";
        }

        if (requested.Count > MaxComments)
        {
            return $"The review has more than {MaxComments} comments; keep the ones that matter most.";
        }

        if (requested.Any(c => c.Body.Length > MaxCommentLength))
        {
            return $"A comment is over {MaxCommentLength} characters; shorten it.";
        }

        try
        {
            var files = await reads.ListPullRequestFilesAsync(owner, repo, number, ct).ConfigureAwait(false);
            if (files is null)
            {
                return $"Pull request #{number} not found in {owner}/{repo} or not accessible.";
            }

            var (kept, dropped) = Anchor(requested, files);
            if (kept.Count == 0 && summary.Length == 0)
            {
                return NothingToPost;
            }

            var draft = new GitHubDraft(
                NewId(), GitHubDraft.ReviewKind, owner, repo, number, string.Empty, summary, kept, [],
                ChatOrigin.Current, time.GetUtcNow(), ActionId: null);
            var what = $"{kept.Count} comment(s) on {draft.Target}";
            var request = $"Post a review on {draft.Target}: {kept.Count} comment(s){Excerpt(summary)}";
            var queued = await QueueAsync(draft, ReviewScope, request, ct).ConfigureAwait(false);
            if (queued is null)
            {
                return UnexpectedlyAllowed;
            }

            if (queued.Kind != WriteGateDecisionKind.Queued)
            {
                return queued.Reason;
            }

            var answer = Queued(queued.ActionId ?? string.Empty, what);
            return dropped.Count == 0
                ? answer
                : answer + " Dropped, not on a line of the diff: " + string.Join(", ", dropped) + ".";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return GitHubPlugin.ErrorLineFor(ex);
        }
    }

    /// <summary>Queue a new issue for the owner's approval.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="title">The issue title.</param>
    /// <param name="body">The issue body, in Markdown.</param>
    /// <param name="labels">Comma-separated label names; empty for none.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>That the issue waits for the owner, or why it was refused.</returns>
    [KernelFunction(CreateIssue)]
    [Description("Queue a new issue on a repository for the owner's approval; it is opened once the owner approves it on the Review screen of Scout Desktop. Only after the user asked for the improvement to be filed. The body says what to change, why, how and where (path:line).")]
    public async Task<string> CreateIssueAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("The issue title, under 256 characters.")] string title,
        [Description("The issue body in Markdown: what, why, how and where.")] string body,
        [Description("Comma-separated label names that exist on the repository; empty for none.")] string labels = "",
        CancellationToken ct = default)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        var heading = (title ?? string.Empty).Trim();
        if (heading.Length == 0 || heading.Length > MaxTitleLength)
        {
            return $"The issue title must be 1 to {MaxTitleLength} characters.";
        }

        var text = (body ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "The issue needs a body: what to change, why, how and where.";
        }

        if (text.Length > MaxBodyLength)
        {
            return $"The issue body is over {MaxBodyLength} characters; shorten it.";
        }

        var names = (labels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count > MaxLabels || names.Any(n => n.Length > 50))
        {
            return $"At most {MaxLabels} labels of up to 50 characters.";
        }

        try
        {
            var draft = new GitHubDraft(
                NewId(), GitHubDraft.IssueKind, owner, repo, 0, heading, text, [], names,
                ChatOrigin.Current, time.GetUtcNow(), ActionId: null);
            var request = $"Open an issue on {draft.Target}: {heading}";
            var queued = await QueueAsync(draft, IssueScope, request, ct).ConfigureAwait(false);
            if (queued is null)
            {
                return UnexpectedlyAllowed;
            }

            return queued.Kind == WriteGateDecisionKind.Queued
                ? Queued(queued.ActionId ?? string.Empty, $"the issue \"{heading}\" on {draft.Target}")
                : queued.Reason;
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return GitHubPlugin.ErrorLineFor(ex);
        }
    }

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
        catch (Exception ex) when (IsHandledFailure(ex))
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
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return GitHubPlugin.ErrorLineFor(ex);
        }
    }

    /// <summary>
    /// Save the draft, then ask the control plane for the irreversible action. Queued: the draft keeps the action id.
    /// Refused: the draft is deleted and the decision returned. Allowed: a misconfigured catalog row, the draft is
    /// deleted and null is returned, because a review or an issue never runs without the owner's approval.
    /// </summary>
    private async Task<WriteGateDecision?> QueueAsync(GitHubDraft draft, string scope, string summary, CancellationToken ct)
    {
        await drafts.SaveAsync(draft, ct).ConfigureAwait(false);
        WriteGateDecision decision;
        try
        {
            decision = await gate.RequestAsync(FeatureId, scope, draft.Target, summary, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await drafts.DeleteAsync(draft.Id).ConfigureAwait(false);
            return new WriteGateDecision(WriteGateDecisionKind.Refused, ControlPlaneUnreachable + " (" + ex.GetType().Name + ")", null);
        }

        switch (decision.Kind)
        {
            case WriteGateDecisionKind.Queued when decision.ActionId is not null:
                await drafts.SaveAsync(draft with { ActionId = decision.ActionId }, ct).ConfigureAwait(false);
                return decision;
            case WriteGateDecisionKind.Refused:
                await drafts.DeleteAsync(draft.Id).ConfigureAwait(false);
                return decision;
            default:
                await drafts.DeleteAsync(draft.Id).ConfigureAwait(false);
                return null;
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

    private static string Excerpt(string body) =>
        body.Length == 0 ? string.Empty : "; " + (body.Length <= SummaryExcerpt ? body : body[..SummaryExcerpt]);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static bool IsHandledFailure(Exception ex)
        => ex is GitHubApiException or HttpRequestException or OperationCanceledException or IOException;

    private sealed record CommentDto(string? Path, int Line, string? Body);
}
