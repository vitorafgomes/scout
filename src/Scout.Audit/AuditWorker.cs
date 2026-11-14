using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;

namespace Scout.Audit;

/// <summary>Wakes the audit worker when a job was started; extra wakes while it is busy are dropped.</summary>
internal sealed class AuditSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Wake() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(CancellationToken ct) => await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
}

/// <summary>
/// Runs the running audit jobs, oldest first, leaving a running review as it is, batch after batch, until each is done or the owner pauses or stops it, after
/// the area in progress: the first
/// pending batch is always the next one, so a job Scout closed in the middle resumes there on the next start. A batch with
/// findings new to the job posts their block into the conversation the job was started from, split into several complete
/// blocks when one would pass <see cref="MaxPostLength"/> characters; the last post, split the same way, carries the
/// verdict and the scope: the areas covered, the files read, and the areas not covered with why. Every batch heading
/// carries the job's findings so far, and a job held by the desktop's local chat also gets one line for an area that ends
/// with no new findings, so the desktop's entry moves with every area; no other conversation gets that line. A post that fails is
/// logged and the job goes on; a batch the provider refused for its usage or rate limit pauses the job, its batch still
/// pending, and posts one sentence saying so, unless the owner paused or stopped it first, whose row it keeps; the owner's
/// own pause or stop posts its one notice when the worker leaves the job for it. Every post goes to the conversation the
/// job holds when it is posted, so a job continued from another chat posts there. A turn cut short by shutdown leaves its batch pending; a job that fails on the database or
/// an unexpected error is logged and waits, still running, for the next start or restart, and the host keeps running.
/// </summary>
internal sealed class AuditWorker(
    IAuditJobStore store,
    AuditBatchRunner runner,
    IOriginNotifier notifier,
    AuditSignal signal,
    ILogger<AuditWorker> logger) : BackgroundService
{
    /// <summary>Characters of one post at most, under the 32,000 the local chat and the desktop cut a message at.</summary>
    internal const int MaxPostLength = 30_000;

    /// <summary>The verdict of a post that covers an area the model did not read in full or that failed, whatever was found.</summary>
    internal const string Incomplete = "incomplete";

    private const int PartLabelRoom = 40;

    /// <summary>The origin prefix of the desktop's local chat, the one conversation told of every area and of a resume.</summary>
    private const string LocalOrigin = "local:";

    /// <summary>Whether the job's posts go to the desktop's local chat.</summary>
    internal static bool IsLocal(AuditJob job) => job.Origin?.StartsWith(LocalOrigin, StringComparison.Ordinal) == true;

    /// <summary>The job's findings so far, as every batch heading and the resume line say them.</summary>
    internal static string SoFar(int findings) =>
        string.Create(CultureInfo.InvariantCulture, $"{findings} {(findings == 1 ? "finding" : "findings")} so far");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var running = await store.RunningJobsAsync(stoppingToken).ConfigureAwait(false);
                    if (running.FirstOrDefault(j => j.Kind == AuditKind.Audit) is { } audit)
                    {
                        await RunAsync(audit, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (AuditStoreException ex)
                {
                    logger.LogError("The audit worker could not use its database: {Reason}", ex.Message);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError("The audit worker stopped a job on an unexpected error ({ExceptionType}).", ex.GetType().Name);
                }

                await signal.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RunAsync(AuditJob job, CancellationToken ct)
    {
        var instructions = await runner.InstructionsAsync(job, ct).ConfigureAwait(false);
        AuditJob? current;
        while ((current = await store.JobAsync(job.Id, ct).ConfigureAwait(false)) is { Status: AuditStatus.Running })
        {
            var (next, count) = await store.NextBatchAsync(job.Id, ct).ConfigureAwait(false);
            if (next is null)
            {
                if (!await store.FinishAsync(job.Id, ct).ConfigureAwait(false))
                {
                    continue;
                }

                var batches = await store.BatchesAsync(job.Id, ct).ConfigureAwait(false);
                var all = await store.FindingsAsync(job.Id, ct).ConfigureAwait(false);
                await PostAllAsync(job.Id, Pieces(Scope(job, batches, all)), ct).ConfigureAwait(false);
                return;
            }

            await store.BeginBatchAsync(job.Id, next.Order, ct).ConfigureAwait(false);
            var titles = (await store.FindingsAsync(job.Id, ct).ConfigureAwait(false))
                .Where(f => next.Files.Contains(f.Path, StringComparer.OrdinalIgnoreCase))
                .Select(f => f.Title)
                .ToList();
            var outcome = await runner.RunAsync(job, instructions, next, count, titles, ct).ConfigureAwait(false);
            if (outcome.Pause)
            {
                if (await store.PauseAsync(job.Id, outcome.Failure!, ct).ConfigureAwait(false))
                {
                    await PostAllAsync(job.Id, [PausedNotice(job, outcome.Failure!)], ct).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            if (outcome.Failure is { } reason)
            {
                await store.NotCoveredAsync(job.Id, next.Order, outcome.FilesRead, reason, ct).ConfigureAwait(false);
                continue;
            }

            var added = await store.CompleteBatchAsync(job.Id, next.Order, outcome.FilesRead, outcome.Findings, ct).ConfigureAwait(false);
            var total = (await store.FindingsAsync(job.Id, ct).ConfigureAwait(false)).Count;
            if (added.Count > 0)
            {
                await PostAllAsync(job.Id, FindingsPosts(job, next, count, outcome, added, total), ct).ConfigureAwait(false);
            }
            else
            {
                await PostAllAsync(job.Id, [NoNewFindings(job, next, count, total)], ct, localOnly: true).ConfigureAwait(false);
            }
        }

        await EndedAsync(current, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The one notice of the owner's own pause or stop, once the worker leaves the job for it: a pause names the reason the
    /// desktop reads as paused, a stop the areas done and the findings kept; nothing for a job paused for another reason,
    /// whose notice the pause itself posted, nor for a done job.
    /// </summary>
    private async Task EndedAsync(AuditJob? job, CancellationToken ct)
    {
        if (job is { Status: AuditStatus.Paused, PausedReason: RepositoryAudits.PausedByOwner })
        {
            await PostAsync(job, OwnerPausedNotice(job), ct).ConfigureAwait(false);
        }
        else if (job is { Status: AuditStatus.Stopped })
        {
            var batches = await store.BatchesAsync(job.Id, ct).ConfigureAwait(false);
            var findings = await store.FindingsAsync(job.Id, ct).ConfigureAwait(false);
            await PostAsync(job, StoppedNotice(job, batches.Count(b => b.Status == AuditStatus.Done), batches.Count, findings.Count), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Post <paramref name="posts"/> in order to the conversation the job holds now, read once before them; with
    /// <paramref name="localOnly"/>, only when that conversation is the desktop's local chat.
    /// </summary>
    private async Task PostAllAsync(string jobId, IEnumerable<string> posts, CancellationToken ct, bool localOnly = false)
    {
        if (await store.JobAsync(jobId, ct).ConfigureAwait(false) is not { } job || (localOnly && !IsLocal(job)))
        {
            return;
        }

        foreach (var post in posts)
        {
            await PostAsync(job, post, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The posts of a batch's new findings: one complete <c>scout-findings</c> block per post, each with the same repository,
    /// commit, verdict and read line and its share of the findings, the verdict <see cref="Incomplete"/> whatever the model
    /// said when the batch's outcome names a file it did not read, every post under <see cref="MaxPostLength"/> characters
    /// heading included, because the chat cuts a longer message and an unclosed fence shows as raw text; the heading names
    /// the job's <paramref name="findings"/> so far.
    /// </summary>
    internal static IReadOnlyList<string> FindingsPosts(AuditJob job, AuditBatch batch, int batchCount, BatchOutcome outcome, IReadOnlyList<AuditFinding> added, int findings)
    {
        var read = string.Create(CultureInfo.InvariantCulture, $"{batch.Area}: {outcome.FilesRead.Count} of {batch.Files.Count} files read");
        var verdict = outcome.Unread.Count > 0 ? Incomplete : outcome.Verdict ?? Verdict(added);
        var heading = Heading(job, batch, batchCount, findings);
        var budget = MaxPostLength - heading.Length - PartLabelRoom;
        var shares = new List<List<AuditFinding>>();
        foreach (var finding in added)
        {
            if (shares.Count > 0 && FindingsBlock.Write(job.Owner, job.Repo, job.Commit, verdict, read, [.. shares[^1], finding]).Length <= budget)
            {
                shares[^1].Add(finding);
            }
            else
            {
                shares.Add([finding]);
            }
        }

        return [.. shares.Select((share, i) =>
            heading
            + (shares.Count == 1 ? ":" : string.Create(CultureInfo.InvariantCulture, $", part {i + 1} of {shares.Count}:"))
            + "\n\n" + FindingsBlock.Write(job.Owner, job.Repo, job.Commit, verdict, read, share))];
    }

    private static string Heading(AuditJob job, AuditBatch batch, int batchCount, int findings) =>
        string.Create(CultureInfo.InvariantCulture, $"Audit of {job.Owner}/{job.Repo}, {batch.Area} ({batch.Order} of {batchCount}, {SoFar(findings)})");

    /// <summary>The one line of an area that ended with no new findings, in the batch heading's form.</summary>
    internal static string NoNewFindings(AuditJob job, AuditBatch batch, int batchCount, int findings) =>
        Heading(job, batch, batchCount, findings) + ": no new findings.";

    /// <summary>The one line of a resumed job: the areas that ended and its findings so far.</summary>
    internal static string ResumedNotice(AuditJob job, int ended, int areas, int findings) =>
        string.Create(CultureInfo.InvariantCulture, $"Audit of {job.Owner}/{job.Repo} resumed: area {ended} of {areas} done, {SoFar(findings)}.");

    /// <summary>
    /// <paramref name="text"/> as posts under <see cref="MaxPostLength"/> characters, cut between lines and, inside a line
    /// that is longer on its own, between the items of its list.
    /// </summary>
    internal static IReadOnlyList<string> Pieces(string text) =>
        Pack(text.Split('\n').SelectMany(line => line.Length <= MaxPostLength ? [line] : Pack(line.Split("; "), "; ")), "\n");

    private static List<string> Pack(IEnumerable<string> parts, string separator)
    {
        var posts = new List<string>();
        foreach (var part in parts)
        {
            if (posts.Count > 0 && posts[^1].Length + separator.Length + part.Length <= MaxPostLength)
            {
                posts[^1] += separator + part;
            }
            else
            {
                posts.Add(part);
            }
        }

        return posts;
    }

    internal static string PausedNotice(AuditJob job, string reason) =>
        $"The audit of {job.Owner}/{job.Repo} paused: {reason}. Ask Scout to continue the audit of {job.Owner}/{job.Repo} when the AI Source has capacity again.";

    internal static string OwnerPausedNotice(AuditJob job) =>
        $"The audit of {job.Owner}/{job.Repo} paused: {RepositoryAudits.PausedByOwner}. Ask Scout to continue the audit of {job.Owner}/{job.Repo} to resume it from the first area not done.";

    internal static string StoppedNotice(AuditJob job, int done, int areas, int findings) =>
        string.Create(CultureInfo.InvariantCulture, $"The audit of {job.Owner}/{job.Repo} ended at your request: {done} of {areas} areas done, {findings} findings kept on the cards.");

    /// <summary>
    /// The job's last post: the verdict, <see cref="Incomplete"/> when an area was not read in full or not covered, with the
    /// counts by severity of what was found either way, how many areas were covered, the files read in full, the files only partly read
    /// because they are longer than a read returns, the areas the model answered without reading every file, with those
    /// files, which are not counted as covered, and the areas not covered with why.
    /// </summary>
    internal static string Scope(AuditJob job, IReadOnlyList<AuditBatch> batches, IReadOnlyList<AuditFinding> findings)
    {
        var unread = batches.Where(b => b.Status == AuditStatus.Done)
            .Select(b => (b.Area, Files: AuditBatchRunner.Unread(b.Files, b.FilesRead)))
            .Where(b => b.Files.Count > 0)
            .ToList();
        var covered = batches.Count(b => b.Status == AuditStatus.Done) - unread.Count;
        var missed = batches.Where(b => b.Status == AuditStatus.NotCovered).ToList();
        var partial = batches.SelectMany(b => b.FilesRead.Intersect(b.Partial ?? [], StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        var read = batches.SelectMany(b => b.FilesRead).Distinct(StringComparer.Ordinal).Except(partial, StringComparer.Ordinal).Count();
        var files = batches.Sum(b => b.Files.Count);
        var count = (string severity) => findings.Count(f => f.Severity == severity);
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Audit of {job.Owner}/{job.Repo} at {job.Commit[..Math.Min(7, job.Commit.Length)]} is done. Verdict: {(unread.Count > 0 || missed.Count > 0 ? Incomplete : Verdict(findings))} ({count("block")} High, {count("fix")} Medium, {count("nit")} Low).\n"
            + $"Scope: {covered} of {batches.Count} areas covered; {read} of {files} files read.");
        if (partial.Count > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $"\nPartly read (first {AuditPlanner.ReadCap:N0} characters): {string.Join("; ", partial)}.");
        }

        if (unread.Count > 0)
        {
            text += "\nNot fully read: " + string.Join("; ", unread.Select(b => $"{b.Area} (not read: {string.Join(", ", b.Files)})")) + ".";
        }

        return (missed.Count, unread.Count) switch
        {
            (0, 0) => text + "\nEvery area was covered.",
            (0, _) => text,
            _ => text + "\nNot covered: " + string.Join("; ", missed.Select(b => $"{b.Area} ({b.Failure})")) + ".",
        };
    }

    private static string Verdict(IReadOnlyList<AuditFinding> findings) =>
        findings.Any(f => f.Severity == "block") ? "sound after the High findings" : "sound";

    private async Task PostAsync(AuditJob job, string text, CancellationToken ct)
    {
        if (job.Origin is null)
        {
            return;
        }

        try
        {
            if (!await notifier.PostAsync(job.Origin, text, ct).ConfigureAwait(false))
            {
                logger.LogWarning("Audit {JobId} could not post to the conversation it was started from.", job.Id);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Audit {JobId} could not post to the conversation it was started from ({ExceptionType}).", job.Id, ex.GetType().Name);
        }
    }
}
