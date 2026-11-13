using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Scout.Audit;

/// <summary>
/// Starts and reports on the review of a pull request too large for one turn as a job of kind review, in sentences the model
/// relays to the owner. Starting takes the repository's snapshot at the pull request's head commit through the GitHub
/// plugin's declared snapshot write, plans the batches from the changed files' patches and keeps the job under Scout's data
/// folder; the status is read from the job's rows. Neither makes a call to GitHub of its own.
/// </summary>
internal interface IPullRequestReviews
{
    /// <summary>
    /// Start a review of pull request <paramref name="number"/> at <paramref name="headSha"/> and answer at once with its plan
    /// and the checks it ran before it started, resume its paused review at that commit, or say why neither happened.
    /// <paramref name="files"/> are every changed file, each with its patch or null when GitHub returned no diff for it;
    /// <paramref name="diffsLeftOut"/> is how many diffs the pull request's answer left out, and none left out starts no
    /// review; <paramref name="checks"/> are the head commit's checks as the caller read them, null when it could not.
    /// </summary>
    Task<ReviewStart> StartAsync(
        string owner, string repo, int number, string headSha, IReadOnlyList<ReviewFile> files, int diffsLeftOut, ReviewCheckCounts? checks, CancellationToken ct);

    /// <summary>How the latest review of the pull request is going, from its rows.</summary>
    Task<string> StatusAsync(string owner, string repo, int number, CancellationToken ct);
}

/// <summary>One changed file of a pull request: its repository-relative path and its patch, null when GitHub returned no diff for it.</summary>
internal sealed record ReviewFile(string Path, string? Patch);

internal sealed partial class RepositoryAudits : IPullRequestReviews
{
    internal const string InvalidPullRequest = "The pull request must be given by its number, above zero.";

    [GeneratedRegex(@"\A[0-9a-fA-F]{7,40}\z")]
    private static partial Regex CommitSha();

    internal const string InvalidHead = "The pull request's head must be a commit SHA of 7 to 40 hexadecimal characters.";

    /// <summary>Why a changed file GitHub gave no patch for is counted as not read.</summary>
    internal const string NoDiff = "GitHub returned no diff";

    /// <summary>The area of the last batch of a review, which holds the files GitHub gave no patch for.</summary>
    internal const string NoDiffArea = "files GitHub returned no diff for";

    /// <summary>The batches a review runs at most; the files of the batches after them are not read.</summary>
    internal const int MaxReviewBatches = 10;

    /// <summary>The minutes a review takes at most, whatever its batches.</summary>
    internal const int MaxReviewMinutes = 60;

    public async Task<ReviewStart> StartAsync(
        string owner, string repo, int number, string headSha, IReadOnlyList<ReviewFile> files, int diffsLeftOut, ReviewCheckCounts? checks, CancellationToken ct)
    {
        if (!IsValid(owner, repo))
        {
            return NotStarted(InvalidRepository);
        }

        if (number <= 0)
        {
            return NotStarted(InvalidPullRequest);
        }

        if (!CommitSha().IsMatch(headSha))
        {
            return NotStarted(InvalidHead);
        }

        var review = Review(owner, repo, number);
        if (diffsLeftOut <= 0)
        {
            return NotStarted($"No {review} was started: the pull request's answer left no diff out, so it is reviewed in this turn.");
        }

        if (!files.Any(f => f.Patch is not null))
        {
            return NotStarted(string.Create(
                CultureInfo.InvariantCulture,
                $"No {review} was started: GitHub returned no diff for any of the {files.Count} changed files, so there is nothing to read in batches."));
        }

        var unread = files.Where(f => f.Patch is null).Select(f => f.Path).ToList();
        List<AuditBatchPlan> plan =
        [
            .. AuditPlanner.Plan(
                [.. files.Where(f => f.Patch is not null).Select(f => new AuditPlanner.Entry(f.Path, f.Patch!.Length))],
                options.MaxBatchFiles,
                options.MaxBatchCharacters),
        ];
        if (unread.Count > 0)
        {
            plan.Add(new AuditBatchPlan(NoDiffArea, unread, 0, Failure: NoDiff));
        }

        List<PreflightCheck> preflight = [Size(files.Count, plan), DockerRuns(ct), Head(headSha, checks)];
        try
        {
            var latest = await store.LatestJobAsync(owner, repo, number, ct).ConfigureAwait(false);
            var same = latest is not null && string.Equals(latest.Commit, headSha, StringComparison.OrdinalIgnoreCase) ? latest : null;
            if (same is { Status: AuditStatus.Paused })
            {
                await store.ResumeAsync(same.Id, origin(), ct).ConfigureAwait(false);
                return new ReviewStart(true, $"Resuming the {review} at {Short(headSha)} from the first batch not done. {await ProgressAsync(same, ct).ConfigureAwait(false)}", ReviewPreflight.None);
            }

            preflight.Add(same switch
            {
                { Status: AuditStatus.Running } => new(PreflightName.Duplicate, PreflightOutcome.Block, $"A {review} at {Short(headSha)} is already running. {await ProgressAsync(same, ct).ConfigureAwait(false)}"),
                { Status: AuditStatus.Done } => new(PreflightName.Duplicate, PreflightOutcome.Block, $"The {review} at {Short(headSha)} is already done. {await ProgressAsync(same, ct).ConfigureAwait(false)}"),
                { Status: AuditStatus.Stopped } => new(PreflightName.Duplicate, PreflightOutcome.Warning, $"A {review} at {Short(headSha)} was stopped; this start reviews it again."),
                _ => new(PreflightName.Duplicate, PreflightOutcome.Ok, $"No other {review} at {Short(headSha)}."),
            });
            var blocks = preflight.Where(c => c.Outcome == PreflightOutcome.Block).Select(c => c.Sentence).ToList();
            if (blocks.Count > 0)
            {
                preflight.Add(CouldNotCheck(PreflightName.Grants, "the grants: the snapshot was not asked for, because the review did not start"));
                return new ReviewStart(false, string.Join(" ", blocks), new ReviewPreflight(preflight));
            }

            var (taken, refusal) = await SnapshotAsync(owner, repo, headSha, review, ct).ConfigureAwait(false);
            if (taken is null)
            {
                preflight.Add(new(PreflightName.Grants, PreflightOutcome.Block, refusal));
                return new ReviewStart(false, refusal, new ReviewPreflight(preflight));
            }

            preflight.Add(new(PreflightName.Grants, PreflightOutcome.Ok, $"The snapshot at {Short(taken.Commit)} was taken through the write gate."));
            if (!string.Equals(taken.Commit, headSha, StringComparison.OrdinalIgnoreCase))
            {
                return new ReviewStart(false, $"The {review} did not start: the snapshot Scout took is at {Short(taken.Commit)}, not at the pull request's head {Short(headSha)}.", new ReviewPreflight(preflight));
            }

            var moved = latest is { Status: AuditStatus.Running or AuditStatus.Paused }
                && await store.StopAsync(owner, repo, number, ct).ConfigureAwait(false)
                ? $" Stopped the review at {Short(latest.Commit)}: the pull request moved from {Short(latest.Commit)} to {Short(taken.Commit)}."
                : string.Empty;
            var kept = new ReviewPreflight(preflight);
            await store.CreateJobAsync(owner, repo, number, taken.Commit, origin(), plan, kept, ct).ConfigureAwait(false);
            var started = string.Create(CultureInfo.InvariantCulture, $"review started: {files.Count} files, {plan.Count} batches, {owner}/{repo}#{number} at {Short(taken.Commit)}.{moved}");
            return new ReviewStart(
                true,
                unread.Count == 0 ? started : string.Create(CultureInfo.InvariantCulture, $"{started} Files GitHub returned no diff for, counted as not read: {unread.Count}."),
                kept);
        }
        catch (AuditStoreException ex)
        {
            return new ReviewStart(false, ex.Message, new ReviewPreflight(preflight));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The review of a pull request could not start ({ExceptionType}).", ex.GetType().Name);
            return new ReviewStart(false, $"Scout could not take the snapshot of {owner}/{repo} ({ex.GetType().Name}).", new ReviewPreflight(preflight));
        }
    }

    /// <summary>
    /// The pull request's size: every changed file and every batch kept, and the most the batches can take, each batch a
    /// turn tried at most twice under the turn time limit and never more than a review's bound; a warning when more batches are planned than a review runs, with
    /// how many files will not be read. An upper bound only, never an expected time.
    /// </summary>
    private PreflightCheck Size(int changed, List<AuditBatchPlan> plan)
    {
        var runnable = plan.Where(b => b.Failure is null).ToList();
        var minutes = Math.Min(Math.Ceiling(Math.Min(runnable.Count, MaxReviewBatches) * AuditBatchRunner.Attempts * options.TurnTimeout.TotalMinutes), MaxReviewMinutes);
        var size = string.Create(CultureInfo.InvariantCulture, $"{changed} files in {plan.Count} batches; at most {minutes} minutes");
        var left = runnable.Skip(MaxReviewBatches).Sum(b => b.Files.Count);
        return left == 0
            ? new(PreflightName.Size, PreflightOutcome.Ok, $"{size}.")
            : new(PreflightName.Size, PreflightOutcome.Warning, string.Create(CultureInfo.InvariantCulture, $"{size}; {left} files will not be read."));
    }

    /// <summary>Whether the owner has Docker runs on, so the review's tests may run; a switch that cannot be read is a warning.</summary>
    private PreflightCheck DockerRuns(CancellationToken ct)
    {
        try
        {
            return environment.ContainerRunsOn
                ? new(PreflightName.DockerRuns, PreflightOutcome.Ok, "Docker runs are on.")
                : new(PreflightName.DockerRuns, PreflightOutcome.Warning, "Docker runs are off; tests will not run.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Scout could not read whether Docker runs are on ({ExceptionType}).", ex.GetType().Name);
            return CouldNotCheck(PreflightName.DockerRuns, "whether Docker runs are on");
        }
    }

    /// <summary>The pinned head commit and its checks as the caller read them, once; counts it could not read are a warning.</summary>
    private static PreflightCheck Head(string headSha, ReviewCheckCounts? checks) => checks switch
    {
        null => CouldNotCheck(PreflightName.Head, "the checks"),
        { Pending: 0, Failing: 0 } => new(PreflightName.Head, PreflightOutcome.Ok, string.Create(CultureInfo.InvariantCulture, $"Head {Short(headSha)}; {checks.Passing} checks passing.")),
        _ => new(PreflightName.Head, PreflightOutcome.Warning, string.Create(
            CultureInfo.InvariantCulture, $"Head {Short(headSha)}; {checks.Passing} checks passing, {checks.Pending} pending, {checks.Failing} failing.")),
    };

    private static PreflightCheck CouldNotCheck(string name, string what) => new(name, PreflightOutcome.Warning, $"Could not check {what}.");

    private static ReviewStart NotStarted(string sentence) => new(false, sentence, ReviewPreflight.None);

    public async Task<string> StatusAsync(string owner, string repo, int number, CancellationToken ct)
    {
        if (!IsValid(owner, repo))
        {
            return InvalidRepository;
        }

        if (number <= 0)
        {
            return InvalidPullRequest;
        }

        try
        {
            if (await store.LatestJobAsync(owner, repo, number, ct).ConfigureAwait(false) is not { } job)
            {
                return $"No {Review(owner, repo, number)} has been started.";
            }

            var state = job is { Status: AuditStatus.Paused, PausedReason: { } reason } ? $"{job.Status}: {reason}" : job.Status;
            return $"The {Review(owner, repo, number)} at {Short(job.Commit)} is {state}. {await ProgressAsync(job, ct).ConfigureAwait(false)}";
        }
        catch (AuditStoreException ex)
        {
            return ex.Message;
        }
    }

    private static string Review(string owner, string repo, int number) => string.Create(CultureInfo.InvariantCulture, $"review of {owner}/{repo}#{number}");

    private static string Short(string commit) => commit[..Math.Min(7, commit.Length)];
}
