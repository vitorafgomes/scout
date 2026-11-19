using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Scout.Audit;

internal sealed partial class AuditWorker
{
    /// <summary>Why a review paused when its pull request could not be read again before a batch.</summary>
    internal const string GitHubUnreadable = "GitHub could not be read";

    /// <summary>The verdict of a review with a blocking finding, however much of it was read.</summary>
    internal const string ReadyAfterBlocking = "ready after the blocking findings";

    /// <summary>The verdict of a review with no blocking finding that read every changed file.</summary>
    internal const string ReadyToMerge = "ready to merge";

    /// <summary>The verdict of a review with no blocking finding that did not read every changed file.</summary>
    internal const string NoBlockingInFilesRead = "no blocking findings in the files read";

    /// <summary>Why the card's tests did not run when no test run is kept for the reviewed commit.</summary>
    internal const string TestsNotKept = "no test run was kept for this commit";

    /// <summary>The findings one review card carries at most, the most severe first.</summary>
    internal const int MaxCardFindings = 50;

    /// <summary>
    /// The characters the card's findings take at most as the JSON sent to <c>present_review_findings</c>, under the block the
    /// plugin accepts, which also holds the checks, the tests and the read line.
    /// </summary>
    internal const int MaxCardFindingsCharacters = 20_000;

    /// <summary>The titles of the job's findings so far a review batch is told at most, the most recent first.</summary>
    internal const int MaxEarlierTitles = 100;

    private static readonly JsonSerializerOptions CardJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Runs a pull request review in waves of up to <see cref="AuditOptions.ReviewParallelBatches"/> pending batches, until
    /// no batch is pending or the job is no longer running. Before each wave: at
    /// <see cref="RepositoryAudits.MaxReviewMinutes"/> minutes of its batches' running time since it last resumed, a paused
    /// time never counted, the job pauses with <see cref="RanForTheBound"/> and one notice, nothing marked not read, and a
    /// continue gives it as many minutes again; then, the first time, the pull request's changed files and patches
    /// are read, with its head, and before every later wave its head commit alone; when GitHub cannot be read the job
    /// pauses with <see cref="GitHubUnreadable"/> and one notice, and when the head moved the wave's batches and every one
    /// after them are kept not read naming both commits; a job no longer running after the head read runs no turn.
    /// Otherwise the wave's batches run together, each one turn handed only its own files' patches and the titles known when
    /// the wave started, and each is saved as it ends, with one progress line to the desktop's local chat only. A provider
    /// limit in any batch of the wave pauses the job once, with one notice, after the others are saved. The end is one card
    /// (<see cref="ReviewCardAsync"/>), built before the job is marked done, so a card that cannot be built leaves the job
    /// running for the next wake, and posted only when the finish took. When the owner paused or stopped the review, its
    /// notice, or the card of what was read, is posted once when the worker leaves it. Every post goes to the conversation
    /// the job holds, never to GitHub.
    /// </summary>
    internal async Task RunReviewAsync(AuditJob job, CancellationToken ct)
    {
        var instructions = await runner.ReviewInstructionsAsync(job, ct).ConfigureAwait(false);
        string? ended = null;
        ReviewPullRequest? read = null;
        AuditJob? current;
        while ((current = await store.JobAsync(job.Id, ct).ConfigureAwait(false)) is { Status: AuditStatus.Running })
        {
            var wave = await store.PendingBatchesAsync(job.Id, _parallel, ct).ConfigureAwait(false);
            if (wave.Count == 0)
            {
                var card = await ReviewCardAsync(current, ct).ConfigureAwait(false);
                if (await store.FinishAsync(job.Id, ct).ConfigureAwait(false))
                {
                    await PostAllAsync(job.Id, [card], ct).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            if (ended is null && await store.RunningTimeAsync(job.Id, ct).ConfigureAwait(false) >= TimeSpan.FromMinutes(RepositoryAudits.MaxReviewMinutes))
            {
                if (await store.PauseAsync(job.Id, RanForTheBound, ct).ConfigureAwait(false))
                {
                    var (done, total, _, _, _) = ReviewCounts(await store.BatchesAsync(job.Id, ct).ConfigureAwait(false));
                    await PostAllAsync(job.Id, [BoundPausedNotice(current, total - done)], ct).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            if (ended is null)
            {
                var fresh = read is null;
                read ??= await ReadAsync(current, ct).ConfigureAwait(false);
                var head = read is null ? null : fresh ? read.HeadSha : await HeadAsync(current, ct).ConfigureAwait(false);
                if (head is null)
                {
                    if (await store.PauseAsync(job.Id, GitHubUnreadable, ct).ConfigureAwait(false))
                    {
                        await PostAllAsync(job.Id, [ReviewPausedNotice(current, GitHubUnreadable)], ct).ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                if (IsJobCommit(current, head))
                {
                    if (await store.JobAsync(job.Id, ct).ConfigureAwait(false) is not { Status: AuditStatus.Running })
                    {
                        continue;
                    }

                    if (!await RunWaveAsync(current, instructions, wave, read!, ct).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                ended = Moved(current.Commit, head);
            }

            foreach (var batch in wave)
            {
                await store.NotCoveredAsync(job.Id, batch.Order, [], ended, ct).ConfigureAwait(false);
            }
        }

        await ReviewLeftAsync(current, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One wave: every batch begun, then their turns run together, each saved as it ends under one lock with its progress
    /// line; false when a batch asked to pause and the job's pause took, its one notice posted after the others were saved.
    /// </summary>
    private async Task<bool> RunWaveAsync(AuditJob job, string instructions, IReadOnlyList<AuditBatch> wave, ReviewPullRequest read, CancellationToken ct)
    {
        var count = (await store.BatchesAsync(job.Id, ct).ConfigureAwait(false)).Count;
        var titles = (await store.FindingsAsync(job.Id, ct).ConfigureAwait(false))
            .Select(f => f.Title)
            .Reverse()
            .Take(MaxEarlierTitles)
            .ToList();
        foreach (var batch in wave)
        {
            await store.BeginBatchAsync(job.Id, batch.Order, ct).ConfigureAwait(false);
        }

        using var saves = new SemaphoreSlim(1, 1);
        string? pause = null;
        await Task.WhenAll(wave.Select(async batch =>
        {
            var patches = read.Files.Where(f => batch.Files.Contains(f.Path, StringComparer.Ordinal)).ToList();
            var outcome = await runner.RunReviewAsync(job, instructions, batch, count, titles, patches, ct).ConfigureAwait(false);
            await saves.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (outcome.Pause)
                {
                    pause ??= outcome.Failure;
                    return;
                }

                if (outcome.Failure is { } reason)
                {
                    await store.NotCoveredAsync(job.Id, batch.Order, outcome.FilesRead, reason, ct).ConfigureAwait(false);
                }
                else
                {
                    await store.CompleteBatchAsync(job.Id, batch.Order, outcome.FilesRead, outcome.Findings, ct).ConfigureAwait(false);
                }

                var batches = await store.BatchesAsync(job.Id, ct).ConfigureAwait(false);
                var items = (await store.FindingsAsync(job.Id, ct).ConfigureAwait(false)).Count;
                await PostAllAsync(job.Id, [ProgressLine(job, ReviewCounts(batches), items)], ct, localOnly: true).ConfigureAwait(false);
            }
            finally
            {
                saves.Release();
            }
        })).ConfigureAwait(false);

        if (pause is not null && await store.PauseAsync(job.Id, pause, ct).ConfigureAwait(false))
        {
            await PostAllAsync(job.Id, [ReviewPausedNotice(job, pause)], ct).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>
    /// What the worker posts when it leaves a review the owner paused or stopped, once: the pause notice, or the card of what
    /// was read with the files not read because the review was stopped, or one sentence when nothing was read.
    /// </summary>
    private async Task ReviewLeftAsync(AuditJob? job, CancellationToken ct)
    {
        if (job is { Status: AuditStatus.Paused, PausedReason: RepositoryAudits.PausedByOwner })
        {
            await PostAllAsync(job.Id, [OwnerPausedReviewNotice(job)], ct).ConfigureAwait(false);
        }
        else if (job is { Status: AuditStatus.Stopped } && !_stoppedCards.ContainsKey(job.Id))
        {
            var read = FilesRead(await store.BatchesAsync(job.Id, ct).ConfigureAwait(false));
            await PostAllAsync(job.Id, [read == 0 ? StoppedEmptyNotice(job) : await ReviewCardAsync(job, ct, stopped: true).ConfigureAwait(false)], ct).ConfigureAwait(false);
            _stoppedCards[job.Id] = true;
        }
    }

    /// <summary>Why a review paused when its running time since it last resumed reached the bound.</summary>
    internal const string RanForTheBound = "it ran for 60 minutes";

    /// <summary>The one notice of a review that paused at its bound of running time, with the batches still to run.</summary>
    internal static string BoundPausedNotice(AuditJob job, int left) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The review of {job.Owner}/{job.Repo}#{job.PullRequest} paused: {RanForTheBound} with {left} batches left. Ask Scout to continue the review of {job.Owner}/{job.Repo}#{job.PullRequest} to give it 60 more minutes.");

    private static bool IsJobCommit(AuditJob job, string head) => string.Equals(head, job.Commit, StringComparison.OrdinalIgnoreCase);

    private async Task<ReviewPullRequest?> ReadAsync(AuditJob job, CancellationToken ct)
    {
        try
        {
            return await pullRequests.ReadAsync(job.Owner, job.Repo, job.PullRequest ?? 0, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Review {JobId} could not read its pull request ({ExceptionType}).", job.Id, ex.GetType().Name);
            return null;
        }
    }

    private async Task<string?> HeadAsync(AuditJob job, CancellationToken ct)
    {
        try
        {
            return await pullRequests.HeadAsync(job.Owner, job.Repo, job.PullRequest ?? 0, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Review {JobId} could not read its pull request's head ({ExceptionType}).", job.Id, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// The review's one post: the verdict from the kept findings' severities (<see cref="ReviewVerdict"/>), the findings that
    /// fit the card (<see cref="CardFindings"/>), presented through <c>present_review_findings</c> with
    /// the read line, then the block completed with the files read of the files changed, the notes and, when the plugin
    /// kept no test run, the tests as not run. The notes are Scout's sentences: each warning the checks kept at the start,
    /// the files not read per reason, a moved head and a bound reached among them, and how many findings the card leaves
    /// out. A block the function refuses, or none, becomes one sentence with the verdict, the files read and the findings kept.
    /// </summary>
    private async Task<string> ReviewCardAsync(AuditJob job, CancellationToken ct, bool stopped = false)
    {
        var batches = await store.BatchesAsync(job.Id, ct).ConfigureAwait(false);
        var findings = await store.FindingsAsync(job.Id, ct).ConfigureAwait(false);
        var preflight = await store.PreflightAsync(job.Id, ct).ConfigureAwait(false);
        var read = FilesRead(batches);
        var of = batches.Sum(b => b.Files.Count);
        var verdict = ReviewVerdict(findings, read == of);
        List<string> notes =
        [
            .. preflight?.Checks.Where(c => c.Outcome == PreflightOutcome.Warning).Select(c => c.Sentence) ?? [],
            .. NotRead(batches, stopped),
        ];
        var shown = CardFindings(findings);
        string? block;
        while (true)
        {
            (block, _) = await runner.PresentReviewAsync(
                job, verdict, JsonSerializer.Serialize(shown, CardJson), string.Create(CultureInfo.InvariantCulture, $"{read} of {of} files read"), ct).ConfigureAwait(false);
            if (block is not null || shown.Count == 0)
            {
                break;
            }

            shown = shown[..(shown.Count / 2)];
        }

        if (shown.Count < findings.Count)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{findings.Count - shown.Count} more findings are not on the card."));
        }

        var review = string.Create(CultureInfo.InvariantCulture, $"{job.Owner}/{job.Repo}#{job.PullRequest} at {Short(job.Commit)}");
        return block is not null && FindingsBlock.Complete(block, read, of, notes, TestsNotKept) is { } card
            ? $"Review of {review}{(stopped ? ", stopped at your request" : string.Empty)}:\n\n{card}"
            : string.Create(CultureInfo.InvariantCulture, $"The review of {review} is done: {verdict}, {read} of {of} files read, {findings.Count} findings kept.");
    }

    /// <summary>
    /// The findings the card carries: the most severe first, while their JSON stays within
    /// <see cref="MaxCardFindingsCharacters"/> and at most <see cref="MaxCardFindings"/> of them.
    /// </summary>
    private static List<AuditFinding> CardFindings(IReadOnlyList<AuditFinding> findings)
    {
        var shown = new List<AuditFinding>();
        var size = 2;
        foreach (var finding in findings.OrderBy(f => Rank(f.Severity)))
        {
            var length = JsonSerializer.Serialize(finding, CardJson).Length + (shown.Count > 0 ? 1 : 0);
            if (shown.Count == MaxCardFindings || size + length > MaxCardFindingsCharacters)
            {
                break;
            }

            shown.Add(finding);
            size += length;
        }

        return shown;
    }

    /// <summary>
    /// The review's verdict from its findings' severities alone: a blocking finding makes it
    /// <see cref="ReadyAfterBlocking"/> however much was read; none makes it <see cref="ReadyToMerge"/> when every changed
    /// file was read and <see cref="NoBlockingInFilesRead"/> when not. Tests that did not run never change it.
    /// </summary>
    internal static string ReviewVerdict(IReadOnlyList<AuditFinding> findings, bool readAll) =>
        findings.Any(f => f.Severity == "block") ? ReadyAfterBlocking : readAll ? ReadyToMerge : NoBlockingInFilesRead;

    /// <summary>The files read in the batches whose turn ended, each once.</summary>
    internal static int FilesRead(IReadOnlyList<AuditBatch> batches) =>
        batches.Where(b => b.Status == AuditStatus.Done).SelectMany(b => b.FilesRead).Distinct(StringComparer.Ordinal).Count();

    /// <summary>
    /// One sentence per reason files were not read, with their count, in the order the batches give the reasons; a batch
    /// still pending in a stopped review was not read because the review was stopped.
    /// </summary>
    private static IEnumerable<string> NotRead(IReadOnlyList<AuditBatch> batches, bool stopped) =>
        batches
            .Select(b => (
                Reason: b.Status == AuditStatus.Done ? AuditBatchRunner.NoPatchGiven : stopped && b.Status == AuditStatus.Pending ? StoppedReason : b.Failure ?? b.Status,
                Files: AuditBatchRunner.Unread(b.Files, b.FilesRead).Count))
            .Where(b => b.Files > 0)
            .GroupBy(b => b.Reason, StringComparer.Ordinal)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Sum(b => b.Files)} files not read: {g.Key}."));

    private static int Rank(string severity) => severity switch
    {
        "block" => 0,
        "fix" => 1,
        "nit" => 2,
        _ => 3,
    };

    /// <summary>Why the files of a review stopped by its owner were not read.</summary>
    internal const string StoppedReason = "the review was stopped";

    /// <summary>
    /// A review's counts as the bell shows them: the batches that can run (never the batches kept not read from the start)
    /// that ended and how many there are, the files read of the files changed, and the area of the first batch still running
    /// or pending, null when none is left.
    /// </summary>
    internal static (int Done, int Total, int Read, int Of, string? Area) ReviewCounts(IReadOnlyList<AuditBatch> batches)
    {
        var runnable = batches.Where(b => b.Area is not (RepositoryAudits.NoDiffArea or RepositoryAudits.DiffTooLargeArea or RepositoryAudits.ControlCharacterArea or RepositoryAudits.NotListedArea)).ToList();
        return (
            runnable.Count(b => b.Status != AuditStatus.Pending),
            runnable.Count,
            FilesRead(batches),
            batches.Sum(b => b.Files.Count),
            runnable.OrderBy(b => b.Order).FirstOrDefault(b => b.Status == AuditStatus.Pending)?.Area);
    }

    /// <summary>The line after each batch, for the desktop's bell only: the batches done, the files read, the findings so far and the area being read.</summary>
    internal static string ProgressLine(AuditJob job, (int Done, int Total, int Read, int Of, string? Area) counts, int items) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Review of {job.Owner}/{job.Repo}#{job.PullRequest}: {counts.Done} of {counts.Total} batches done, {counts.Read} of {counts.Of} files read, {SoFar(items)}{(counts.Area is { } area ? $", reading {area}" : string.Empty)}.");

    /// <summary>The line the desktop's local chat gets when the owner continues a paused review.</summary>
    internal static string ReviewResumedNotice(AuditJob job, (int Done, int Total, int Read, int Of, string? Area) counts, int items) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Review of {job.Owner}/{job.Repo}#{job.PullRequest} resumed: {counts.Done} of {counts.Total} batches done, {counts.Read} of {counts.Of} files read, {SoFar(items)}.");

    /// <summary>The notice of a review the owner paused, posted once when the worker leaves it.</summary>
    internal static string OwnerPausedReviewNotice(AuditJob job) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The review of {job.Owner}/{job.Repo}#{job.PullRequest} paused: {RepositoryAudits.PausedByOwner}. Ask Scout to continue the review of {job.Owner}/{job.Repo}#{job.PullRequest} to resume it from the first batch not done.");

    /// <summary>The one sentence of a review the owner stopped before any file was read.</summary>
    internal static string StoppedEmptyNotice(AuditJob job) =>
        string.Create(CultureInfo.InvariantCulture, $"The review of {job.Owner}/{job.Repo}#{job.PullRequest} ended at your request: nothing was read.");

    internal static string ReviewPausedNotice(AuditJob job, string reason) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The review of {job.Owner}/{job.Repo}#{job.PullRequest} paused: {reason}. Ask Scout to review {job.Owner}/{job.Repo}#{job.PullRequest} again to resume it from the first batch not done.");

    private static string Moved(string from, string to) => $"the pull request moved from {Short(from)} to {Short(to)}";

    private static string Short(string commit) => commit[..Math.Min(7, commit.Length)];
}
