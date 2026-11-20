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
public interface IPullRequestReviews
{
    /// <summary>
    /// Start a review of pull request <paramref name="number"/> at <paramref name="headSha"/> and answer at once with its plan
    /// and the checks it ran before it started, resume its paused review at that commit, or say why neither happened.
    /// <paramref name="files"/> are every changed file, each with its patch or null when GitHub returned no diff for it;
    /// <paramref name="diffsLeftOut"/> is how many diffs the pull request's answer left out, and none left out starts no
    /// review; <paramref name="notListed"/> is how many changed files GitHub did not list, each counted as not read;
    /// <paramref name="checks"/> are the head commit's checks as the caller read them, null when it could not;
    /// <paramref name="purpose"/> is what the pull request says it does, null when it was not given. A new review keeps the
    /// purpose, the project's rule files and what the pull request removed for its batches; a resumed one keeps those it
    /// started with.
    /// </summary>
    Task<ReviewStart> StartAsync(
        string owner, string repo, int number, string headSha, IReadOnlyList<ReviewFile> files, int diffsLeftOut, int notListed, ReviewCheckCounts? checks, CancellationToken ct,
        ReviewPurpose? purpose = null);

    /// <summary>How the latest review of the pull request is going, from its rows.</summary>
    Task<string> StatusAsync(string owner, string repo, int number, CancellationToken ct);

    /// <summary>
    /// The owner's pause, continue or stop of the latest review of the pull request, as Scout Desktop asks it: whether it
    /// took and the sentence the owner reads. A batch in progress finishes first; continue only resumes a paused review.
    /// </summary>
    Task<AuditControlAnswer> ControlAsync(string owner, string repo, int number, string action, CancellationToken ct);
}

/// <summary>
/// One changed file of a pull request: its repository-relative path, its patch, null when GitHub returned no diff for it,
/// and its status as GitHub gives it, such as <c>removed</c>, null when it was not given.
/// </summary>
public sealed record ReviewFile(string Path, string? Patch, string? Status = null);

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

    /// <summary>Why a changed file whose patch is longer than one batch holds is counted as not read.</summary>
    internal const string DiffTooLarge = "diff larger than one batch";

    /// <summary>Why a changed file whose path holds a control character is counted as not read; the path never reaches a request.</summary>
    internal const string ControlCharacterPath = "its path has a control character";

    /// <summary>The area of the batch that holds the files whose path holds a control character, first of the batches kept not covered.</summary>
    internal const string ControlCharacterArea = "files whose path has a control character";

    /// <summary>The area of the batch that holds the files whose patch is longer than one batch holds, before the files with no diff.</summary>
    internal const string DiffTooLargeArea = "files whose diff is larger than one batch";

    /// <summary>Why a changed file GitHub counts but did not list is counted as not read.</summary>
    internal const string NotListed = "GitHub did not list these files";

    /// <summary>The area of the batch that stands for the changed files GitHub did not list, last of the batches kept not covered.</summary>
    internal const string NotListedArea = "files GitHub did not list";

    /// <summary>The minutes a review takes at most, whatever its batches.</summary>
    internal const int MaxReviewMinutes = 60;

    public async Task<ReviewStart> StartAsync(
        string owner, string repo, int number, string headSha, IReadOnlyList<ReviewFile> files, int diffsLeftOut, int notListed, ReviewCheckCounts? checks, CancellationToken ct,
        ReviewPurpose? purpose = null)
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

        var cap = options.MaxBatchCharacters;
        var controlled = files.Where(f => f.Path.Any(char.IsControl)).Select(f => f.Path).ToList();
        var unread = files.Where(f => f.Patch is null && !controlled.Contains(f.Path)).Select(f => f.Path).ToList();
        var sized = files.Where(f => f.Patch is not null && !controlled.Contains(f.Path)).Select(f => new AuditPlanner.Entry(f.Path, AuditBatchRunner.SentLength(f.Path, f.Patch!) + 1)).ToList();
        if (sized.All(f => f.Size > cap - 1))
        {
            return NotStarted(NothingToRead(review, files.Count, unread.Count, sized.Count, controlled.Count, cap));
        }

        notListed = Math.Max(0, notListed);
        var plan = PlanIn(cap - 1);

        var changed = files.Count + notListed;

        List<PreflightCheck> preflight = [Size(changed, plan), DockerRuns(ct), Head(headSha, checks)];
        try
        {
            var latest = await store.LatestJobAsync(owner, repo, number, ct).ConfigureAwait(false);
            var same = latest is not null && string.Equals(latest.Commit, headSha, StringComparison.OrdinalIgnoreCase) ? latest : null;
            if (same is { Status: AuditStatus.Paused })
            {
                await store.ResumeAsync(same.Id, origin(), ct).ConfigureAwait(false);
                signal.Wake();
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

            var inputs = Handed(new ReviewInputs(
                purpose is null ? null : ReviewInputs.Bound(purpose), ReviewRules.Read(taken.Root, [.. files.Select(f => f.Path)]), ReviewRemoved.From(files, taken.Root)));
            if (inputs is not null)
            {
                plan = PlanIn(cap - 1 - AuditBatchRunner.InputsLength(inputs));
                preflight[preflight.FindIndex(c => c.Name == PreflightName.Size)] = Size(changed, plan);
                if (plan.All(b => b.Failure is not null))
                {
                    return new ReviewStart(false, NothingToRead(review, files.Count, unread.Count, sized.Count, controlled.Count, cap), new ReviewPreflight(preflight));
                }
            }

            var moved = latest is { Status: AuditStatus.Running or AuditStatus.Paused }
                && await store.StopAsync(owner, repo, number, ct).ConfigureAwait(false)
                ? $" Stopped the review at {Short(latest.Commit)}: the pull request moved from {Short(latest.Commit)} to {Short(taken.Commit)}."
                : string.Empty;
            var kept = new ReviewPreflight(preflight);
            await store.CreateJobAsync(owner, repo, number, taken.Commit, origin(), plan, kept, ct, inputs).ConfigureAwait(false);
            signal.Wake();
            var started = string.Create(
                CultureInfo.InvariantCulture,
                $"review started: {changed} files in {plan.Count} batches, {owner}/{repo}#{number} at {Short(taken.Commit)}; code first, {options.ReviewParallelBatches} batches at a time. Follow it in the bell of Scout Desktop; every finding arrives here once, when the review ends.{moved}");
            var noDiff = unread.Count == 0 ? started : string.Create(CultureInfo.InvariantCulture, $"{started} Files GitHub returned no diff for, counted as not read: {unread.Count}.");
            return new ReviewStart(
                true,
                notListed == 0 ? noDiff : string.Create(CultureInfo.InvariantCulture, $"{noDiff} Files GitHub did not list, counted as not read: {notListed}."),
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

        List<AuditBatchPlan> PlanIn(long room)
        {
            var tooLarge = sized.Where(f => f.Size > room).ToList();
            List<AuditBatchPlan> batches =
            [
                .. sized.Except(tooLarge).GroupBy(f => ReadingOrder(f.Path)).OrderBy(g => g.Key)
                    .SelectMany(g => AuditPlanner.Plan([.. g], options.MaxBatchFiles, room)),
            ];
            if (controlled.Count > 0)
            {
                batches.Add(new AuditBatchPlan(ControlCharacterArea, controlled, 0, Failure: ControlCharacterPath));
            }

            if (tooLarge.Count > 0)
            {
                batches.Add(new AuditBatchPlan(DiffTooLargeArea, [.. tooLarge.Select(f => f.Path)], tooLarge.Sum(f => f.Size), Failure: DiffTooLarge));
            }

            if (unread.Count > 0)
            {
                batches.Add(new AuditBatchPlan(NoDiffArea, unread, 0, Failure: NoDiff));
            }

            if (notListed > 0)
            {
                batches.Add(new AuditBatchPlan(NotListedArea, [.. Enumerable.Range(1, notListed).Select(i => string.Create(CultureInfo.InvariantCulture, $"(file {i} GitHub did not list)"))], 0, Failure: NotListed));
            }

            return batches;
        }
    }

    /// <summary>
    /// The inputs a review keeps, or none when they hold nothing to hand a batch: no purpose, no rule file named or
    /// unreadable, and nothing removed or uncounted; such a review is planned and asked as one that kept none.
    /// </summary>
    private static ReviewInputs? Handed(ReviewInputs inputs) =>
        inputs is { Purpose: null, Rules: { Unreadable: false, Files.Count: 0, NotIncluded.Count: 0 }, Removed: { Entries.Count: 0, TestFilesWithoutDiff: 0, RepositoryNotSearched: false } }
            ? null
            : inputs;

    /// <summary>
    /// The pull request's size: every changed file and every batch kept, and the most the batches can take, each wave of
    /// <see cref="AuditOptions.ReviewParallelBatches"/> batches tried at most twice under the turn time limit, and never more
    /// than a review's 60 minutes. An upper bound only, never an expected time.
    /// </summary>
    private PreflightCheck Size(int changed, List<AuditBatchPlan> plan)
    {
        var waves = Math.Ceiling(plan.Count(b => b.Failure is null) / (double)options.ReviewParallelBatches);
        var minutes = Math.Min(Math.Ceiling(waves * AuditBatchRunner.Attempts * options.TurnTimeout.TotalMinutes), MaxReviewMinutes);
        return new(PreflightName.Size, PreflightOutcome.Ok, string.Create(CultureInfo.InvariantCulture, $"{changed} files in {plan.Count} batches; at most {minutes} minutes."));
    }

    /// <summary>
    /// Where a changed file comes in a review's reading order: 0 source code, 1 tests, 2 build and configuration files,
    /// 3 documentation. Documentation is a file with a documentation extension, or any file under a <c>docs</c>,
    /// <c>context</c> or <c>.claude</c> folder; a test is a file under a test folder, in a project whose name ends
    /// <c>.Tests</c>, or named as a test; the rest is code, its build and configuration files after the tests.
    /// </summary>
    internal static int ReadingOrder(string path)
    {
        var segments = path.Split('/');
        var name = segments[^1];
        var folders = segments[..^1];
        if (DocumentationExtensions.Contains(Path.GetExtension(name)) || folders.Any(DocumentationFolders.Contains))
        {
            return 3;
        }

        if (folders.Any(f => TestFolders.Contains(f) || f.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
            || name.Contains(".test.", StringComparison.OrdinalIgnoreCase) || name.Contains(".spec.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Tests.cs", StringComparison.Ordinal))
        {
            return 1;
        }

        return ConfigurationExtensions.Contains(Path.GetExtension(name)) || name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }

    private static readonly HashSet<string> DocumentationExtensions = new([".md", ".mdx", ".txt", ".rst", ".adoc", ".html"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DocumentationFolders = new(["docs", "context", ".claude"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TestFolders = new(["test", "tests", "spec", "e2e", "__tests__"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ConfigurationExtensions = new([".json", ".yml", ".yaml", ".toml", ".xml", ".props", ".lock"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why no review starts when none of the changed files is left to read in batches: how many, and the count per reason,
    /// in Scout's words and never a path, with the batch's size in tokens for a diff too large.
    /// </summary>
    private static string NothingToRead(string review, int changed, int noDiff, int tooLarge, int controlled, long cap)
    {
        List<string> reasons =
        [
            .. noDiff > 0 ? [string.Create(CultureInfo.InvariantCulture, $"{noDiff} with no diff from GitHub")] : Array.Empty<string>(),
            .. tooLarge > 0 ? [string.Create(CultureInfo.InvariantCulture, $"{tooLarge} with a diff larger than one batch of about {Tokens(cap):N0} tokens")] : Array.Empty<string>(),
            .. controlled > 0 ? [string.Create(CultureInfo.InvariantCulture, $"{controlled} with a control character in the path")] : Array.Empty<string>(),
        ];
        var counted = reasons.Count == 0 ? "." : $": {string.Join("; ", reasons)}.";
        return string.Create(CultureInfo.InvariantCulture, $"No {review} was started: none of the {changed} changed files can be read in batches{counted}");
    }

    private static long Tokens(long characters) => (characters + CharactersPerToken - 1) / CharactersPerToken;

    /// <summary>The characters a token is counted as when a batch's size is told in tokens.</summary>
    internal const int CharactersPerToken = 4;

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

    public async Task<AuditControlAnswer> ControlAsync(string owner, string repo, int number, string action, CancellationToken ct)
    {
        if (!IsValid(owner, repo))
        {
            return new AuditControlAnswer(false, InvalidRepository);
        }

        if (number <= 0)
        {
            return new AuditControlAnswer(false, InvalidPullRequest);
        }

        var review = Review(owner, repo, number);
        var resume = $"Ask Scout to continue the {review} to resume it from the first batch not done.";
        try
        {
            var job = await store.LatestJobAsync(owner, repo, number, ct).ConfigureAwait(false);
            switch (action)
            {
                case Pause when job is { Status: AuditStatus.Running } && await store.PauseAsync(job.Id, PausedByOwner, ct).ConfigureAwait(false):
                    return new AuditControlAnswer(true, $"Paused the {review}; a batch in progress finishes first. {await ProgressAsync(job, ct).ConfigureAwait(false)} {resume}");
                case Pause:
                    job = job is null ? null : await store.JobAsync(job.Id, ct).ConfigureAwait(false);
                    return new AuditControlAnswer(false, job is { Status: AuditStatus.Paused }
                        ? $"The {review} is already paused{(job.PausedReason is { } reason ? $": {reason}" : string.Empty)}. {await ProgressAsync(job, ct).ConfigureAwait(false)} {resume}"
                        : $"No {review} is running.");
                case Continue when job is { Status: AuditStatus.Paused }:
                    await store.ResumeAsync(job.Id, origin(), ct).ConfigureAwait(false);
                    await PostReviewResumedAsync(job.Id, ct).ConfigureAwait(false);
                    signal.Wake();
                    return new AuditControlAnswer(true, $"Resuming the {review} at {Short(job.Commit)} from the first batch not done. {await ProgressAsync(job, ct).ConfigureAwait(false)}");
                case Continue:
                    return new AuditControlAnswer(false, job is { Status: AuditStatus.Running }
                        ? $"The {review} is already running. {await ProgressAsync(job, ct).ConfigureAwait(false)}"
                        : $"No paused {review} to continue.");
                case Stop when await store.StopAsync(owner, repo, number, ct).ConfigureAwait(false):
                    AuditJob? stopped;
                    try
                    {
                        stopped = await store.LatestJobAsync(owner, repo, number, ct).ConfigureAwait(false);
                    }
                    catch (AuditStoreException)
                    {
                        if (job is not null)
                        {
                            signal.Stopped(job.Id);
                        }

                        return new AuditControlAnswer(true, $"Stopped the {review}.");
                    }

                    if (stopped is null)
                    {
                        return new AuditControlAnswer(true, $"Stopped the {review}.");
                    }

                    signal.Stopped(stopped.Id);
                    return new AuditControlAnswer(true, $"Stopped the {review}; a batch in progress finishes first, and the card of what was read arrives in the chat. {await ProgressAsync(stopped, ct).ConfigureAwait(false)}");
                case Stop:
                    return new AuditControlAnswer(false, $"No {review} is running.");
                default:
                    return new AuditControlAnswer(false, $"Scout does not {action} a review.");
            }
        }
        catch (AuditStoreException ex)
        {
            return new AuditControlAnswer(false, ex.Message);
        }
    }

    /// <summary>
    /// The resume line of a review, only to a job the desktop's local chat holds after the resume, so its entry turns
    /// running at once; a post or a read that fails is logged and the resume stands.
    /// </summary>
    private async Task PostReviewResumedAsync(string jobId, CancellationToken ct)
    {
        if (notifier is null)
        {
            return;
        }

        try
        {
            if (await store.JobAsync(jobId, ct).ConfigureAwait(false) is not { } job || !AuditWorker.IsLocal(job))
            {
                return;
            }

            var counts = AuditWorker.ReviewCounts(await store.BatchesAsync(jobId, ct).ConfigureAwait(false));
            var items = (await store.FindingsAsync(jobId, ct).ConfigureAwait(false)).Count;
            if (!await notifier().PostAsync(job.Origin!, AuditWorker.ReviewResumedNotice(job, counts, items), ct).ConfigureAwait(false))
            {
                logger.LogWarning("Review {JobId} could not post its resume to the conversation it holds.", jobId);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Review {JobId} could not post its resume to the conversation it holds ({ExceptionType}).", jobId, ex.GetType().Name);
        }
    }

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
