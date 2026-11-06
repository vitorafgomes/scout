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
/// The writes the model may ask for around GitHub, each declared on its own row of the <c>github</c> feature and run
/// only under the owner's grant: <c>submit_pull_request_review</c> posts a commenting review on a pull request and
/// <c>create_issue</c> opens an issue, both at once, the answer carrying the URL GitHub gave; the write gate refuses
/// either before anything is sent while the owner has not allowed GitHub writes, and records every run.
/// <c>remember_project_conventions</c> is a write into Scout's data folder: the conventions profile of a repository
/// an audit judges the code against, read back with <c>get_project_conventions</c>; <c>snapshot_repository</c> is
/// another: the repository at one commit, extracted under that folder so every read of it is local and the Knowledge
/// index can search it. The model is told what GitHub accepted, never more: a review or an issue that was not
/// posted is a <see cref="WriteFailedException"/>, which the write gate filter turns into the answer and records as
/// failed.
/// </summary>
internal sealed class GitHubActionsPlugin(
    IGitHubClient reads,
    IGitHubWriteClient writes,
    GitHubProfileStore profiles,
    RepositorySnapshotStore snapshots,
    IRepositoryProjects projects,
    TimeProvider time)
{
    internal const string PluginName = "GitHubActions";
    internal const string SubmitReview = "submit_pull_request_review";
    internal const string CreateIssue = "create_issue";
    internal const string RememberConventions = "remember_project_conventions";
    internal const string GetConventions = "get_project_conventions";
    internal const string SnapshotRepository = "snapshot_repository";

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

    /// <summary>Post a commenting review on a pull request, under the owner's grant.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="body">The review's summary text; may be empty when there are comments.</param>
    /// <param name="comments">JSON array of comments: path, line (in the file after the change), body.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>That the review was posted, with its URL and the comments dropped for pointing outside the diff, or why it was refused.</returns>
    [KernelFunction(SubmitReview)]
    [Description("Post a code review on a pull request: a summary and comments anchored to lines of the pull request's diff, as a commenting review (never an approval or a request for changes). Only after the user asked for the review to be posted. The answer carries the review's URL. A comment whose path and line are not in the diff is dropped and named in the answer.")]
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
            throw Refused(GitHubNames.InvalidRepository);
        }

        if (number <= 0)
        {
            throw Refused("The pull request number must be positive.");
        }

        var requested = ParseComments(comments) ?? throw Refused(InvalidComments);
        var summary = (body ?? string.Empty).Trim();
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
            throw Refused($"Pull request #{number.ToString(CultureInfo.InvariantCulture)} not found in {owner}/{repo} or not accessible.");
        }

        var (kept, dropped) = Anchor(requested, files);
        if (kept.Count == 0 && summary.Length == 0)
        {
            throw Refused(NothingToPost);
        }

        PostedReview posted;
        try
        {
            posted = await writes.SubmitReviewAsync(owner, repo, number, summary, kept, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: true);
        }

        var answer = $"Posted a review on {owner}/{repo}#{number.ToString(CultureInfo.InvariantCulture)} with {kept.Count} comment(s): {posted.HtmlUrl}";
        return dropped.Count == 0
            ? answer
            : answer + " Dropped, not on a line of the diff: " + string.Join(", ", dropped) + ".";
    }

    /// <summary>Open an issue on a repository, under the owner's grant.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="title">The issue title.</param>
    /// <param name="body">The issue body, in Markdown.</param>
    /// <param name="labels">Comma-separated label names; empty for none.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>That the issue was opened, with its number and URL, or why it was refused.</returns>
    [KernelFunction(CreateIssue)]
    [Description("Open an issue on a repository. Only after the user asked for the improvement to be filed. The body says what to change, why, how and where (path:line). The answer carries the issue's number and URL.")]
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

        CreatedIssue created;
        try
        {
            created = await writes.CreateIssueAsync(owner, repo, heading, text, names, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            throw Failed(ex, posted: true);
        }

        return $"Opened issue #{created.Number.ToString(CultureInfo.InvariantCulture)} on {owner}/{repo}: {created.HtmlUrl}";
    }

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
        if (snapshots.FindBySha(owner, repo, commit.Sha) is { } existing)
        {
            if (existing.Reference != commit.Reference || existing.IsDefaultBranch != commit.IsDefaultBranch)
            {
                existing = await RepositorySnapshotStore.RelabelAsync(existing, commit, ct).ConfigureAwait(false);
            }

            projects.Register(name, existing.Root);
            return Taken(existing, name, already: true);
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
            // A stream that breaks mid-download surfaces as an IOException; the store has removed its staging folder.
            throw Failed(ex, posted: false);
        }

        projects.Register(name, taken.Root);
        return Taken(taken, name, already: false);
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
