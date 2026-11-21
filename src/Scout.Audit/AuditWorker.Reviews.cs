using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Scout.Review;

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

    /// <summary>Why a review the owner stopped before its tests ended carries no result.</summary>
    internal const string TestsStoppedWithReview = "The review was stopped before its tests ended; the run was cut short and no result was kept.";


    /// <summary>How long the card waits for the review's test run after the batches end, unless set; declared before the sentence that names it.</summary>
    internal static readonly TimeSpan DefaultTestsWait = TimeSpan.FromMinutes(25);

    /// <summary>Why a run still going when the card's wait ended carries no result.</summary>
    internal static readonly string TestsOverTheWait = string.Create(
        CultureInfo.InvariantCulture, $"The tests did not end within {DefaultTestsWait.TotalMinutes} minutes of the review's end; the run was cut short and no result was kept.");

    /// <summary>The second line of a review's progress while its tests run.</summary>
    internal const string TestsRunning = "The pull request's tests are running in a container on this computer.";

    /// <summary>What one run of a review covers: the one command Scout chose, never a second stack's tests.</summary>
    internal const string OneRunCovers =
        "One run covers the one test command Scout chose for the repository's stack, from the conventions profile, a CI workflow or the stack's default; the tests of another stack in the same repository are not run.";

    /// <summary>How long the card waits at most for the review's tests: the sandbox's 20 minutes and the steps before it.</summary>
    internal TimeSpan TestsWait { get; init; } = DefaultTestsWait;

    /// <summary>
    /// The test run of each review this worker ran, by job id, kept in memory: a review resumed in this process starts no
    /// second run, and a review run again after a restart of the worker runs its tests again, since nothing of the run is
    /// kept across a restart.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Task<string> Answer, CancellationTokenSource Cut)> _tests = new(StringComparer.Ordinal);

    /// <summary>Whether this worker still holds a test run for <paramref name="jobId"/>.</summary>
    internal bool HoldsTests(string jobId) => _tests.ContainsKey(jobId);

    /// <summary>The card's note on focused readings that could not be completed; null when every one was.</summary>
    internal static string? FocusedNote(int count) => count switch
    {
        <= 0 => null,
        1 => "1 focused reading could not be completed; its findings may be missing.",
        _ => string.Create(CultureInfo.InvariantCulture, $"{count} focused readings could not be completed; their findings may be missing."),
    };

    /// <summary>
    /// Forgets the review's run once its card is posted or the review ended: cuts it if it still goes, and frees its token
    /// when the run has ended, so a run cut here never meets a disposed token.
    /// </summary>
    private void ForgetTests(string jobId)
    {
        if (_tests.TryRemove(jobId, out var run))
        {
            run.Cut.Cancel();
            _ = run.Answer.ContinueWith(_ => run.Cut.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Starts the review's one test run in this process, not awaited, unless it was already started.</summary>
    private void StartTests(AuditJob job, CancellationToken ct)
    {
        lock (_tests)
        {
            if (runner.CanRunTests && !_tests.ContainsKey(job.Id))
            {
                var cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _tests[job.Id] = (Task.Run(() => runner.RunTestsAsync(job, cut.Token), CancellationToken.None), cut);
            }
        }
    }

    /// <summary>
    /// Waits for the review's run, at most <see cref="TestsWait"/>, cutting it short when the owner stopped the review or the
    /// wait ended; answers why the card carries no run when none was kept: the run's own reason, the gate's refusal, or why
    /// it was cut. A kept run reaches the card through the findings function and this reason is then not used.
    /// </summary>
    private async Task<(string Reason, TestRunFacts? Facts)> TestsReasonAsync(AuditJob job, bool stopped, CancellationToken ct)
    {
        if (!_tests.TryGetValue(job.Id, out var run))
        {
            return (TestsNotKept, null);
        }

        if (stopped)
        {
            await run.Cut.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            var answer = await run.Answer.WaitAsync(TestsWait, ct).ConfigureAwait(false);
            return (ReasonOf(answer), FactsOf(answer));
        }
        catch (TimeoutException)
        {
            await run.Cut.CancelAsync().ConfigureAwait(false);
            return (TestsOverTheWait, new TestRunFacts(false, TestsOverTheWait, null, null, [], false));
        }
    }

    /// <summary>The facts of the run of the last card this worker built, for the stage that reads them with the findings; null when the review ran no test.</summary>
    internal TestRunFacts? LastCardTests { get; private set; }

    private static readonly JsonSerializerOptions AnswerJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The facts of a run's answer: the function's JSON read field by field, or, for any other answer such as the gate's
    /// refusal, a run that did not run for that sentence.
    /// </summary>
    internal static TestRunFacts FactsOf(string answer)
    {
        try
        {
            using var parsed = JsonDocument.Parse(answer);
            var root = parsed.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ran", out var ran) && ran.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var projects = root.TryGetProperty("projects", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.Deserialize<List<TestProjectFacts>>(AnswerJson) ?? []
                    : [];
                return new TestRunFacts(
                    ran.GetBoolean(),
                    Text("reason"),
                    Text("command"),
                    root.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number ? exit.GetInt32() : null,
                    projects,
                    root.TryGetProperty("projectsPartial", out var partial) && partial.ValueKind == JsonValueKind.True);
            }
        }
        catch (JsonException)
        {
        }

        return new TestRunFacts(false, ReasonOf(answer), null, null, [], false);
    }

    /// <summary>The reason a run's answer gives when it did not run, or the answer itself when it is a sentence, such as the gate's refusal.</summary>
    internal static string ReasonOf(string answer)
    {
        try
        {
            using var parsed = JsonDocument.Parse(answer);
            return parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()!
                : TestsNotKept;
        }
        catch (JsonException)
        {
            return answer.Length <= FindingLimits.MaxNoteLength ? answer : answer[..FindingLimits.MaxNoteLength];
        }
    }

    /// <summary>The second line of a review's progress: whether its tests run, and how they ended once they did.</summary>
    private string? TestsLine(AuditJob job)
    {
        if (!_tests.TryGetValue(job.Id, out var run))
        {
            return null;
        }

        if (!run.Answer.IsCompletedSuccessfully)
        {
            return TestsRunning;
        }

        try
        {
            using var parsed = JsonDocument.Parse(run.Answer.Result);
            var root = parsed.RootElement;
            return root.TryGetProperty("ran", out var ran) && ran.ValueKind == JsonValueKind.True
                ? root.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.GetInt32() == 0 ? "The pull request's tests passed." : "The pull request's tests failed or could not all run; the card says which."
                : $"The pull request's tests did not run: {ReasonOf(run.Answer.Result)}";
        }
        catch (JsonException)
        {
            return $"The pull request's tests did not run: {ReasonOf(run.Answer.Result)}";
        }
    }

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
    /// Runs a pull request review through a window of up to <see cref="AuditOptions.ReviewParallelBatches"/> batches in flight (<see cref="RunWindowAsync"/>), until
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
        await StampAsync(job, ct).ConfigureAwait(false);
        if (signal.TookWaiting(job.Id))
        {
            var items = (await store.FindingsAsync(job.Id, ct).ConfigureAwait(false)).Count;
            await PostAllAsync(job.Id, [ProgressLine(job, ReviewCounts(await store.BatchesAsync(job.Id, ct).ConfigureAwait(false)), items)], ct, localOnly: true).ConfigureAwait(false);
        }

        StartTests(job, ct);
        var instructions = await runner.ReviewInstructionsAsync(job, ct).ConfigureAwait(false);
        var inputs = await store.InputsAsync(job.Id, ct).ConfigureAwait(false);
        var constant = inputs is null ? null : AuditBatchRunner.ConstantBlock(inputs);
        string? ended = null;
        ReviewPullRequest? read = null;
        AuditJob? current;
        while ((current = await store.JobAsync(job.Id, ct).ConfigureAwait(false)) is { Status: AuditStatus.Running })
        {
            var wave = await store.PendingBatchesAsync(job.Id, _parallel, ct).ConfigureAwait(false);
            if (wave.Count == 0)
            {
                var card = await ReviewCardAsync(current, ct, pullRequest: read).ConfigureAwait(false);
                if (await store.FinishAsync(job.Id, ct).ConfigureAwait(false))
                {
                    await PostAllAsync(job.Id, [card], ct).ConfigureAwait(false);
                    ForgetTests(job.Id);
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

                    if (!await RunWindowAsync(current, instructions, inputs, constant, read!, ct).ConfigureAwait(false))
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
    /// The review's batches through a window of at most <see cref="AuditOptions.ReviewParallelBatches"/> in flight: when one
    /// ends, the next pending batch in the plan's order starts, so a batch that hangs holds one place, never the others.
    /// Before each start: the job must still be running, its running time under <see cref="RepositoryAudits.MaxReviewMinutes"/>,
    /// and, every time as many batches as the window holds have started, its head still the commit reviewed; otherwise no
    /// batch starts and the ones in flight end, for the run to say why. Each batch is begun, handed its own files' patches
    /// and the titles of the findings saved when it starts, and saved as it ends under one lock with its progress line. A
    /// provider limit stops starting batches; the job pauses once, with one notice, after the ones in flight are saved, and
    /// false is answered. Each batch is handed the <paramref name="inputs"/> kept at the review's start, with their
    /// <paramref name="constant"/> block built once per run.
    /// </summary>
    private async Task<bool> RunWindowAsync(
        AuditJob job, string instructions, ReviewInputs? inputs, string? constant, ReviewPullRequest read, CancellationToken ct)
    {
        var count = (await store.BatchesAsync(job.Id, ct).ConfigureAwait(false)).Count;
        using var saves = new SemaphoreSlim(1, 1);
        string? pause = null;
        var started = new HashSet<int>();
        var all = new List<Task>();
        var inFlight = new List<Task>();
        var sinceHead = 0;
        var starting = true;
        while (true)
        {
            inFlight.RemoveAll(t => t.IsCompleted);
            await OthersLeftAsync(job.Id, ct).ConfigureAwait(false);
            if (starting && pause is null && inFlight.Count < _parallel)
            {
                var next = await NextBatchAsync(job, started, inFlight.Count + 1, sinceHead, ct).ConfigureAwait(false);
                if (next is { Batch: { } batch })
                {
                    sinceHead = next.Value.SinceHead + 1;
                    started.Add(batch.Order);
                    await store.BeginBatchAsync(job.Id, batch.Order, ct).ConfigureAwait(false);
                    var titles = (await store.FindingsAsync(job.Id, ct).ConfigureAwait(false))
                        .Select(f => f.Title)
                        .Reverse()
                        .Take(MaxEarlierTitles)
                        .ToList();
                    var task = RunWindowBatchAsync(job, instructions, inputs, constant, read, batch, count, titles, saves, reason => pause ??= reason, ct);
                    all.Add(task);
                    inFlight.Add(task);
                    continue;
                }

                starting = false;
            }

            if (inFlight.Count == 0)
            {
                break;
            }

            await Task.WhenAny(inFlight).ConfigureAwait(false);
        }

        await Task.WhenAll(all).ConfigureAwait(false);
        if (pause is not null && await store.PauseAsync(job.Id, pause, ct).ConfigureAwait(false))
        {
            await PostAllAsync(job.Id, [ReviewPausedNotice(job, pause)], ct).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The next pending batch of <paramref name="job"/> not yet started in this run, in the plan's order, with the count of
    /// batches started since the head was last read; null when no batch may start now: the job is no longer running, its
    /// running time reached the bound, its head could not be read or moved, or no pending batch is left to start.
    /// </summary>
    private async Task<(AuditBatch? Batch, int SinceHead)?> NextBatchAsync(AuditJob job, HashSet<int> started, int look, int sinceHead, CancellationToken ct)
    {
        if (await store.JobAsync(job.Id, ct).ConfigureAwait(false) is not { Status: AuditStatus.Running }
            || await store.RunningTimeAsync(job.Id, ct).ConfigureAwait(false) >= TimeSpan.FromMinutes(RepositoryAudits.MaxReviewMinutes))
        {
            return null;
        }

        var pending = await store.PendingBatchesAsync(job.Id, started.Count + look, ct).ConfigureAwait(false);
        if (pending.FirstOrDefault(b => !started.Contains(b.Order)) is not { } next)
        {
            return null;
        }

        if (sinceHead >= _parallel)
        {
            if (await HeadAsync(job, ct).ConfigureAwait(false) is not { } head || !IsJobCommit(job, head)
                || await store.JobAsync(job.Id, ct).ConfigureAwait(false) is not { Status: AuditStatus.Running })
            {
                return null;
            }

            sinceHead = 0;
        }

        return (next, sinceHead);
    }

    /// <summary>One batch of the window: its turn, then its save and progress line under <paramref name="saves"/>; a provider limit is handed to <paramref name="paused"/>.</summary>
    private async Task RunWindowBatchAsync(
        AuditJob job, string instructions, ReviewInputs? inputs, string? constant, ReviewPullRequest read, AuditBatch batch, int count, List<string> titles,
        SemaphoreSlim saves, Action<string?> paused, CancellationToken ct)
    {
        var patches = read.Files.Where(f => batch.Files.Contains(f.Path, StringComparer.Ordinal)).ToList();
        if (RepositoryAudits.PartOf(batch.Area) is { } part)
        {
            var file = patches.FirstOrDefault(f => f.Path == part.Path);
            var parts = file?.Patch is null ? null : RepositoryAudits.PartsOf(part.Path, file.Patch, RepositoryAudits.Room(_batchCharacters, inputs));
            if (file is null || parts is null || parts.Count != part.Of || parts[part.Part - 1].Patch is not { } partPatch)
            {
                await saves.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await store.NotCoveredAsync(job.Id, batch.Order, [], RepositoryAudits.PartChanged, ct).ConfigureAwait(false);
                }
                finally
                {
                    saves.Release();
                }

                return;
            }

            patches = [file with { Patch = partPatch }];
        }

        var outcome = await runner.RunReviewAsync(job, instructions, batch, count, titles, patches, ct, inputs, constant).ConfigureAwait(false);
        await saves.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (outcome.Pause)
            {
                paused(outcome.Failure);
                return;
            }

            if (outcome.Failure is { } reason)
            {
                await store.NotCoveredAsync(job.Id, batch.Order, outcome.FilesRead, reason, ct).ConfigureAwait(false);
            }
            else
            {
                await store.CompleteBatchAsync(job.Id, batch.Order, outcome.FilesRead, outcome.Findings, outcome.LeftOut, ct).ConfigureAwait(false);
            }

            var batches = await store.BatchesAsync(job.Id, ct).ConfigureAwait(false);
            var items = (await store.FindingsAsync(job.Id, ct).ConfigureAwait(false)).Count;
            await PostAllAsync(job.Id, [TestsLine(job) is { } tests ? $"{ProgressLine(job, ReviewCounts(batches), items)}\n{tests}" : ProgressLine(job, ReviewCounts(batches), items)], ct, localOnly: true).ConfigureAwait(false);
        }
        finally
        {
            saves.Release();
        }
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
            ForgetTests(job.Id);
        }
    }

    /// <summary>
    /// The stopped cards of the reviews the owner stopped while <paramref name="runningId"/> runs, a waiting one or a paused
    /// one, posted as a batch of the running review ends rather than after it; the running review's own stop is its run's to say.
    /// </summary>
    private async Task OthersLeftAsync(string runningId, CancellationToken ct)
    {
        while (signal.TryTakeStopped(out var stopped))
        {
            if (stopped != runningId
                && await store.JobAsync(stopped, ct).ConfigureAwait(false) is { Kind: AuditKind.Review, Status: AuditStatus.Stopped } review)
            {
                await ReviewLeftAsync(review, ct).ConfigureAwait(false);
            }
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
    /// The review's one post: the verdict and the offer from one decision over the kept findings, the files read, the run and the checks (<see cref="ReviewOffers.Decide"/>, <see cref="VerdictOf"/>), the findings that
    /// fit the card (<see cref="CardFindings"/>), presented through <c>present_review_findings</c> with
    /// the read line, then the block completed with the files read of the files changed, the notes and, when the plugin
    /// kept no test run, the tests as not run. The notes are Scout's sentences: each warning the checks kept at the start,
    /// the inputs' counts of what was removed and of what could not be read, the files not read per reason, a moved head and a bound reached among them, and how many findings the card leaves
    /// out. A block the function refuses, or none, becomes one sentence with the verdict, the files read and the findings kept.
    /// </summary>
    private async Task<string> ReviewCardAsync(AuditJob job, CancellationToken ct, bool stopped = false, ReviewPullRequest? pullRequest = null)
    {
        var (testsReason, facts) = await TestsReasonAsync(job, stopped, ct).ConfigureAwait(false);
        LastCardTests = facts;
        var batches = await store.BatchesAsync(job.Id, ct).ConfigureAwait(false);
        var inputs = await store.InputsAsync(job.Id, ct).ConfigureAwait(false);
        var (findings, secondReading, unconfirmed, noted) = await SecondReadingAsync(job, await store.FindingsAsync(job.Id, ct).ConfigureAwait(false), facts, batches, stopped, inputs?.Purpose, pullRequest, ct).ConfigureAwait(false);
        var preflight = await store.PreflightAsync(job.Id, ct).ConfigureAwait(false);
        var read = FilesRead(batches);
        var of = FilesChanged(batches);
        var offer = ReviewOffers.Decide(new OfferInput(
            [.. findings.Select(f => new OfferFinding(f.Severity, unconfirmed.Contains(f.Id)))], read, of, facts, await ChecksAsync(job, ct).ConfigureAwait(false)));
        var verdict = VerdictOf(offer);
        var totals = await store.TurnTotalsAsync(job.Id, ct).ConfigureAwait(false);
        List<string> notes =
        [
            .. preflight?.Checks.Where(c => c.Outcome == PreflightOutcome.Warning).Select(c => c.Sentence) ?? [],
            .. secondReading,
            .. inputs?.Notes() ?? [],
            .. NotRead(batches, stopped),
            .. LeftOut(batches),
            .. FocusedNote(totals.Where(t => t.Reading is ReviewReadings.Removed or ReviewReadings.Requests).Sum(t => t.NotCompleted)) is { } focused ? [focused] : Array.Empty<string>(),
            .. AuditBatchRunner.AskedNote(totals.Sum(t => t.Turns)) is { } asked ? [asked] : Array.Empty<string>(),
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
        return block is not null && FindingsBlock.Complete(block, read, of, ReviewNotes.Fit(notes), testsReason, offer, unconfirmed, noted) is { } card
            ? $"Review of {review}{(stopped ? ", stopped at your request" : string.Empty)}:\n\n{card}"
            : string.Create(CultureInfo.InvariantCulture, $"The review of {review} is done: {verdict}, {read} of {of} files read, {findings.Count} findings kept.");
    }

    /// <summary>The progress line of a review whose findings Scout's second reading is checking.</summary>
    internal static string SecondReadingLine(int findings) => findings == 1
        ? "Scout's second reading is checking 1 finding against the code, the definitions it calls and the run's facts."
        : string.Create(CultureInfo.InvariantCulture, $"Scout's second reading is checking {findings} findings against the code, the definitions they call and the run's facts.");

    /// <summary>The time the second reading of one review may take in all; shorter only in tests.</summary>
    internal TimeSpan ContestTime { get; init; } = Contestation.MaxTime;

    /// <summary>The reading name the second reading's turns are kept under.</summary>
    internal const string Contest = "contest";

    /// <summary>Writes the Scout version and the chat model that run <paramref name="job"/> now; a write that fails is logged and the job goes on.</summary>
    private async Task StampAsync(AuditJob job, CancellationToken ct)
    {
        try
        {
            await store.StampRunAsync(job.Id, AuditBatchRunner.ScoutVersion, runner.ChatModel, ct).ConfigureAwait(false);
        }
        catch (AuditStoreException)
        {
            logger.LogWarning("Audit {JobId}: the version and model that run it could not be written.", job.Id);
        }
    }

    /// <summary>The time one group of the second reading may take; the batch turn's timeout unless set.</summary>
    internal TimeSpan? ContestGroupTime { get; init; }

    /// <summary>Why the second reading did not run for a group whose answer was not one JSON array.</summary>
    internal const string ContestUnreadable = "its answer could not be read.";

    /// <summary>Why the second reading did not run for a group whose call failed.</summary>
    internal const string ContestFailed = "the call to the model failed.";

    /// <summary>Why the second reading did not run for findings past its time.</summary>
    internal static readonly string ContestOverTime = string.Create(CultureInfo.InvariantCulture, $"it reached its {Contestation.MaxTime.TotalMinutes} minutes.");

    /// <summary>Why the second reading did not run for findings past its groups.</summary>
    internal static readonly string ContestOverGroups = string.Create(CultureInfo.InvariantCulture, $"it reached its {Contestation.MaxGroups} requests.");

    /// <summary>Why the second reading did not run for the findings of a review its owner stopped.</summary>
    internal const string ContestStopped = "the review was stopped.";

    /// <summary>
    /// Scout's second reading of a review's findings, before its card: each finding still <c>open</c> is handed, in
    /// groups (<see cref="Contestation.Groups"/>) bounded by the batch's characters, to a model turn offered the three
    /// reads of the code at the head (<see cref="AuditBatchRunner.ContestOffered"/>), with the code around it and the definitions of the names its evidence calls read from the job's snapshot, and the
    /// run's facts. Each answer is checked by code (<see cref="Contestation.Read"/>) and recorded; a group whose answer is
    /// unreadable, whose call fails or runs out of time is left as it was. Returns the findings the card keeps, each as its
    /// recorded outcome makes it (<see cref="Contestation.Apply"/>), and the notes on what the reading did.
    /// </summary>
    private async Task<(IReadOnlyList<Finding> Kept, IReadOnlyList<string> Notes, IReadOnlySet<string> Unconfirmed, IReadOnlySet<string> Noted)> SecondReadingAsync(
        AuditJob job, IReadOnlyList<Finding> stored, TestRunFacts? facts, IReadOnlyList<AuditBatch> batches, bool stopped, ReviewPurpose? purpose,
        ReviewPullRequest? pullRequest, CancellationToken ct)
    {
        var states = await store.FindingStatesAsync(job.Id, ct).ConfigureAwait(false);
        var open = stored.Where(f => !states.ContainsKey(f.Id)).OrderBy(f => Rank(f.Severity)).ToList();
        var outcomes = states
            .Where(s => ContestStatus.StatusOf(s.Value.State) is not null)
            .ToDictionary(
                s => s.Key,
                s => new ContestOutcome(ContestStatus.StatusOf(s.Value.State)!, s.Value.Reason ?? string.Empty, stored.FirstOrDefault(f => f.Id == s.Key)?.Severity),
                StringComparer.Ordinal);
        var refutedGroups = new List<(int Refuted, string Title, string Reason)>();
        if (stored.FirstOrDefault(f => outcomes.TryGetValue(f.Id, out var o) && o.Status == ContestStatus.Refuted) is { } earlier)
        {
            refutedGroups.Add((outcomes.Count(o => o.Value.Status == ContestStatus.Refuted), earlier.Title, outcomes[earlier.Id].Reason));
        }

        var notContested = new List<(int Findings, string Reason, IReadOnlyList<string> Titles)>();
        if (open.Count > 0 && stopped)
        {
            notContested.Add((open.Count, ContestStopped, [.. open.Select(f => f.Title)]));
        }
        else if (open.Count > 0)
        {
            await PostAllAsync(job.Id, [$"{ProgressLine(job, ReviewCounts(batches), stored.Count)}\n{SecondReadingLine(open.Count)}"], ct, localOnly: true).ConfigureAwait(false);
            var root = snapshots?.Find(job.Owner, job.Repo, job.Commit)?.Root;
            var asks = ContestRequest.Asks(purpose?.Title, purpose?.Description, Template(root));
            var room = Math.Max(0, _contestCharacters - ContestRequest.Of(facts, asks, []).Length);
            pullRequest ??= await ReadAsync(job, ct).ConfigureAwait(false);
            var patches = pullRequest is not null && string.Equals(pullRequest.HeadSha, job.Commit, StringComparison.OrdinalIgnoreCase)
                ? pullRequest.Files.GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Patch, StringComparer.Ordinal)
                : [];
            var materials = open.ToDictionary(
                f => f.Id,
                f =>
                {
                    var names = CalledNames.In(f.Evidence);
                    return ContestRequest.Material(
                        f, CodeAround(root, f), DiffLines.HunkHolding(patches.GetValueOrDefault(f.Path), f.Line, f.Evidence), DefinitionReader.For(root, names).Text(), DefinitionReader.TestsFor(root, f.Path, names), room);
                },
                StringComparer.Ordinal);
            var groups = Contestation.Groups(open, f => materials[f.Id].Length, room);
            string[] changedFiles = [.. batches.SelectMany(b => b.Files).Distinct(StringComparer.Ordinal)];
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var left = ContestTime - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                var bound = left < (ContestGroupTime ?? runner.TurnTimeout) ? left : ContestGroupTime ?? runner.TurnTimeout;
                if (i >= Contestation.MaxGroups || left <= TimeSpan.Zero)
                {
                    notContested.Add((groups.Skip(i).Sum(g => g.Count), i >= Contestation.MaxGroups ? ContestOverGroups : ContestOverTime, [.. groups.Skip(i).SelectMany(g => g).Select(f => f.Title)]));
                    break;
                }

                var contestRequest = ContestRequest.ReadAt(job.Owner, job.Repo, job.Commit) + "\n" + ContestRequest.Of(facts, asks, group.Select(f => materials[f.Id]));
                var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
                var turnStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var (answer, failure) = await ContestGroupAsync(job, contestRequest, history, bound, ct).ConfigureAwait(false);
                var answered = failure is null ? Checked(Contestation.Read(answer, [.. group.Select(f => f.Id)]), root, stored, changedFiles) : null;
                await runner.CountAsync(job, new AuditTurn(
                    null, Contest, i + 1, 1, false, (long)System.Diagnostics.Stopwatch.GetElapsedTime(turnStarted).TotalMilliseconds, contestRequest.Length,
                    AuditBatchRunner.CallCounts(history), group.Count, 0, 0, failure ?? (answered is null ? ContestUnreadable : null),
                    answered?.Count(o => o.Value.Status == ContestStatus.Holds), answered?.Count(o => o.Value.Status == ContestStatus.Refuted),
                    answered?.Count(o => o.Value.Status == ContestStatus.Uncertain))).ConfigureAwait(false);
                runner.Record(job, string.Create(CultureInfo.InvariantCulture, $"contest-{i + 1}"), new
                {
                    requestLength = contestRequest.Length,
                    findings = group.Select(f => f.Id),
                    calls = AuditBatchRunner.CallsOf(history),
                    results = AuditBatchRunner.ResultsOf(history),
                    answer,
                    failure,
                    outcomes = failure is null ? Contestation.Read(answer, [.. group.Select(f => f.Id)])?.ToDictionary(o => o.Key, o => new { status = o.Value.Status, reason = o.Value.Reason }) : null,
                });
                if (failure is not null)
                {
                    notContested.Add((group.Count, failure, [.. group.Select(f => f.Title)]));
                    continue;
                }

                if (answered is not { } read)
                {
                    notContested.Add((group.Count, ContestUnreadable, [.. group.Select(f => f.Title)]));
                    continue;
                }

                await store.RecordOutcomesAsync(job.Id, read, ct).ConfigureAwait(false);
                foreach (var (id, outcome) in read)
                {
                    outcomes[id] = outcome;
                }

                if (group.FirstOrDefault(f => read[f.Id].Status == ContestStatus.Refuted) is { } first)
                {
                    refutedGroups.Add((read.Count(r => r.Value.Status == ContestStatus.Refuted), first.Title, read[first.Id].Reason));
                }
            }
        }

        var kept = stored.Select(f => Contestation.Apply(f, outcomes.GetValueOrDefault(f.Id))).OfType<Finding>().ToList();
        var uncertain = stored.Where(f => outcomes.GetValueOrDefault(f.Id)?.Status == ContestStatus.Uncertain).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var noted = stored.Where(f => outcomes.GetValueOrDefault(f.Id)?.Status == ContestStatus.Noted).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var changed = stored.Count(f => outcomes.GetValueOrDefault(f.Id) is { Status: ContestStatus.Holds, Severity: { } severity } && severity != f.Severity);
        return (kept, Contestation.Notes(refutedGroups, uncertain.Count, [.. notContested.GroupBy(n => n.Reason).Select(g => (g.Sum(n => n.Findings), g.Key, (IReadOnlyList<string>)[.. g.SelectMany(n => n.Titles)]))], changed, noted.Count), uncertain, noted);
    }

    /// <summary>
    /// <paramref name="read"/> with each refutation and note whose cited line the snapshot at <paramref name="root"/> does not
    /// hold made uncertain (<see cref="Contestation.Checked"/>), a cited name resolved against the finding's own path and the
    /// review's <paramref name="changed"/> files; each file is read once, within the snapshot. As it is when there is no
    /// snapshot to check against.
    /// </summary>
    private static IReadOnlyDictionary<string, ContestOutcome>? Checked(
        IReadOnlyDictionary<string, ContestOutcome>? read, string? root, IReadOnlyList<Finding> stored, IReadOnlyCollection<string> changed)
    {
        if (read is null || root is null)
        {
            return read;
        }

        var counts = new Dictionary<string, int?>(StringComparer.Ordinal);
        int? Lines(string path) => counts.TryGetValue(path, out var known) ? known
            : counts[path] = SnapshotFile(root, path) is { } text ? text.Split('\n').Length - (text.EndsWith('\n') ? 1 : 0) : null;
        return read.ToDictionary(
            o => o.Key, o => Contestation.Checked(o.Value, stored.FirstOrDefault(f => f.Id == o.Key)?.Path ?? string.Empty, changed, Lines), StringComparer.Ordinal);
    }

    private async Task<(string? Answer, string? Failure)> ContestGroupAsync(
        AuditJob job, string request, Microsoft.SemanticKernel.ChatCompletion.ChatHistory history, TimeSpan left, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(left);
        try
        {
            return (await runner.ContestAsync(job, request, history, limit.Token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, ContestOverTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Review {JobId}: Scout's second reading failed ({ExceptionType}); its findings are kept as they were.", job.Id, ex.GetType().Name);
            return (null, ContestFailed);
        }
    }

    /// <summary>
    /// The lines of the finding's file around its line from the job's snapshot, numbered; null when the snapshot does not
    /// hold the file as a plain file inside it. The text is data from the pull request.
    /// </summary>
    private static string? CodeAround(string? root, Finding finding) =>
        SnapshotFile(root, finding.Path) is { } text ? ContestRequest.Around(text.Split('\n'), finding.Line) : null;

    /// <summary>Where a repository keeps its pull request template, in the order they are looked for.</summary>
    private static readonly string[] TemplatePaths =
        [".github/pull_request_template.md", ".github/PULL_REQUEST_TEMPLATE.md", "pull_request_template.md", "PULL_REQUEST_TEMPLATE.md", "docs/pull_request_template.md", "docs/PULL_REQUEST_TEMPLATE.md"];

    /// <summary>The repository's pull request template at the job's head, the first of <see cref="TemplatePaths"/> it holds; null when none.</summary>
    private static string? Template(string? root) => TemplatePaths.Select(p => SnapshotFile(root, p)).FirstOrDefault(t => t is not null);

    /// <summary>
    /// The text of <paramref name="relative"/> in the snapshot at <paramref name="root"/>, read only as a plain file inside it,
    /// never through a link, at most 1 MB; null otherwise. The text is data from the pull request.
    /// </summary>
    private static string? SnapshotFile(string? root, string relative)
    {
        if (root is null)
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var inside = full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var file = new FileInfo(full);
            return inside && file.Exists && file.LinkTarget is null && file.Length <= 1_048_576 ? File.ReadAllText(full).ReplaceLineEndings("\n") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The findings the card carries: the most severe first, while their JSON stays within
    /// <see cref="MaxCardFindingsCharacters"/> and at most <see cref="MaxCardFindings"/> of them.
    /// </summary>
    private static List<Finding> CardFindings(IReadOnlyList<Finding> findings)
    {
        var shown = new List<Finding>();
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
    /// The review's verdict sentence from its offer, so the two never disagree: <see cref="ReadyAfterBlocking"/> when it
    /// requests changes, <see cref="ReadyToMerge"/> when it approves or waits for the checks to approve, and
    /// <see cref="NoBlockingInFilesRead"/> when it only comments or offers nothing.
    /// </summary>
    internal static string VerdictOf(Offer offer) => offer.Verdict switch
    {
        ReviewOffers.RequestChanges => ReadyAfterBlocking,
        ReviewOffers.Approve or ReviewOffers.Wait => ReadyToMerge,
        _ => NoBlockingInFilesRead,
    };

    /// <summary>The checks of the job's commit for its offer; null when they could not be read, so the offer never approves.</summary>
    private async Task<OfferChecks?> ChecksAsync(AuditJob job, CancellationToken ct)
    {
        try
        {
            return await pullRequests.ChecksAsync(job.Owner, job.Repo, job.Commit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Review {JobId} could not read its checks ({ExceptionType}).", job.Id, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The files read in the batches whose turn ended, each once.</summary>
    internal static int FilesRead(IReadOnlyList<AuditBatch> batches)
    {
        var read = batches.Where(b => b.Status == AuditStatus.Done).SelectMany(b => b.FilesRead).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, parts) in PartsByFile(batches))
        {
            if (PartsRead(path, parts) < RepositoryAudits.PartOf(parts[0].Area)!.Value.Of)
            {
                read.Remove(path);
            }
        }

        return read.Count;
    }

    /// <summary>The files the pull request changes: each batch's files, a file read in parts counted once.</summary>
    internal static int FilesChanged(IReadOnlyList<AuditBatch> batches) =>
        batches.Where(b => RepositoryAudits.PartOf(b.Area) is null).Sum(b => b.Files.Count) + PartsByFile(batches).Count();

    /// <summary>The part batches of each file read in parts, by path.</summary>
    private static IEnumerable<(string Path, List<AuditBatch> Parts)> PartsByFile(IReadOnlyList<AuditBatch> batches) =>
        batches.Where(b => RepositoryAudits.PartOf(b.Area) is not null)
            .GroupBy(b => RepositoryAudits.PartOf(b.Area)!.Value.Path, StringComparer.Ordinal)
            .Select(g => (g.Key, g.ToList()));

    /// <summary>How many parts of <paramref name="path"/> were read: the part batches that ended having read it.</summary>
    private static int PartsRead(string path, List<AuditBatch> parts) =>
        parts.Count(b => b.Status == AuditStatus.Done && b.FilesRead.Contains(path, StringComparer.Ordinal));

    /// <summary>
    /// One sentence per reason files were not read, with their count, in the order the batches give the reasons; a batch
    /// still pending in a stopped review was not read because the review was stopped.
    /// </summary>
    internal static IEnumerable<string> NotRead(IReadOnlyList<AuditBatch> batches, bool stopped)
    {
        var whole = batches
            .Where(b => RepositoryAudits.PartOf(b.Area) is null)
            .Select(b => (
                Reason: b.Status == AuditStatus.Done ? AuditBatchRunner.NoPatchGiven : stopped && b.Status == AuditStatus.Pending ? StoppedReason : b.Failure ?? b.Status,
                Files: AuditBatchRunner.Unread(b.Files, b.FilesRead).Count))
            .Where(b => b.Files > 0)
            .GroupBy(b => b.Reason, StringComparer.Ordinal)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Sum(b => b.Files)} files not read: {g.Key}."));
        foreach (var note in whole)
        {
            yield return note;
        }

        foreach (var (path, parts) in PartsByFile(batches))
        {
            var of = RepositoryAudits.PartOf(parts[0].Area)!.Value.Of;
            var read = PartsRead(path, parts);
            if (read == of)
            {
                continue;
            }

            yield return string.Create(CultureInfo.InvariantCulture, $"{read} of {of} parts of {path} were read.");
            foreach (var failed in parts.Where(b => b.Failure is not null))
            {
                yield return failed.Failure!.StartsWith("parts ", StringComparison.Ordinal)
                    ? string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(failed.Failure[0])}{failed.Failure[1..]}.")
                    : string.Create(CultureInfo.InvariantCulture, $"Part {RepositoryAudits.PartOf(failed.Area)!.Value.Part} of {path} was not read: {failed.Failure}.");
            }
        }
    }

    /// <summary>
    /// One note per batch that left findings out, naming its area, at most <see cref="MaxLeftOutNotes"/> of them and then
    /// one line counting the other batches; each note cut to <see cref="FindingLimits.MaxNoteLength"/>.
    /// </summary>
    internal static IReadOnlyList<string> LeftOut(IReadOnlyList<AuditBatch> batches)
    {
        return ReviewNotes.Capped(
            [.. batches.Where(b => b.LeftOut is not null).Select(b => ReviewNotes.Cut($"{b.Area}: {b.LeftOut}"))],
            MaxLeftOutNotes,
            more => more == 1 ? "1 more batch left findings out." : string.Create(CultureInfo.InvariantCulture, $"{more} more batches left findings out."));
    }

    /// <summary>A severity's place in the card's order, the most severe first.</summary>
    internal static int Rank(string severity) => severity switch
    {
        Severities.Block => 0,
        Severities.Fix => 1,
        Severities.Nit => 2,
        _ => 3,
    };

    /// <summary>Why the files of a review stopped by its owner were not read.</summary>
    internal const string StoppedReason = "the review was stopped";

    /// <summary>
    /// The notes on findings left out a card shows before it counts the rest: with the other notes at their most (five
    /// preflight warnings, nine on the inputs, one on the findings not on the card) they leave room for one reason files
    /// were not read within <see cref="FindingLimits.MaxNotes"/>.
    /// </summary>
    internal const int MaxLeftOutNotes = 3;

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
            FilesChanged(batches),
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
