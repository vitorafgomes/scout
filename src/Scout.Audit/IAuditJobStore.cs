using Scout.Review;

namespace Scout.Audit;

/// <summary>The status words a job or a batch is kept under.</summary>
internal static class AuditStatus
{
    internal const string Running = "running";
    internal const string Stopped = "stopped";
    internal const string Paused = "paused";
    internal const string Done = "done";
    internal const string Pending = "pending";
    internal const string NotCovered = "not covered";
}

/// <summary>The kinds of job: an audit of a repository, or a review of one of its pull requests.</summary>
internal static class AuditKind
{
    internal const string Audit = "audit";
    internal const string Review = "review";
}

/// <summary>
/// One job of one repository at one commit, the chat it was started from, whether it is running, paused, stopped or done,
/// why it paused, its kind, the pull request a review is of, and when it was started and finished, in UTC by the store's clock.
/// </summary>
internal sealed record AuditJob(
    string Id, string Owner, string Repo, string Commit, string? Origin, string Status, string? PausedReason = null, string Kind = AuditKind.Audit, int? PullRequest = null,
    DateTime? Started = null, DateTime? Finished = null);

/// <summary>
/// One batch of a job: its order, its area and files, whether it is pending, done or not covered, the files the model read,
/// why it was not covered, the files longer than a read returns, and the sentence on the findings it left out, null when
/// it left none out.
/// </summary>
internal sealed record AuditBatch(
    int Order, string Area, IReadOnlyList<string> Files, string Status, IReadOnlyList<string> FilesRead, string? Failure, IReadOnlyList<string>? Partial = null,
    string? LeftOut = null);

/// <summary>The audit database could not be opened, read or written; the message names the operation and the SQLite error code only.</summary>
public sealed class AuditStoreException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>
/// Keeps the audit jobs: a job, its batches in order and its findings. A job resumes from its first pending batch; a
/// finding whose path and title, whatever their case, are already in the job is not added again; a finding whose id
/// another finding of the job holds gets a free id. Every failure of the database is an <see cref="AuditStoreException"/>.
/// </summary>
internal interface IAuditJobStore
{
    /// <summary>
    /// Keep a running job with its batches in one transaction, each pending, or not covered with its reason when the plan gave
    /// it a failure, and the checks a review ran before it started and the inputs its batches are handed in the same save;
    /// answers the job's id. A job with <paramref name="pullRequest"/> is a review of that pull request, one without it an
    /// audit, which keeps no checks and no inputs.
    /// </summary>
    Task<string> CreateJobAsync(
        string owner, string repo, int? pullRequest, string commit, string? origin, IReadOnlyList<AuditBatchPlan> batches, ReviewPreflight? preflight, CancellationToken ct,
        ReviewInputs? inputs = null);

    /// <summary>
    /// The running jobs in the order they joined the worker's queue: when each was created, or last continued after a
    /// pause, the earlier first; the worker runs the first, so every other running review waits.
    /// </summary>
    Task<IReadOnlyList<AuditJob>> RunningJobsAsync(CancellationToken ct);

    /// <summary>The JSON of what became of Scout's earlier findings for the review <paramref name="jobId"/>; null before the check ran.</summary>
    Task<string?> EarlierAsync(string jobId, CancellationToken ct);

    /// <summary>Keep <paramref name="json"/>, what became of Scout's earlier findings, with the review <paramref name="jobId"/>.</summary>
    Task SaveEarlierAsync(string jobId, string json, CancellationToken ct);

    /// <summary>The job with <paramref name="id"/>, null when there is none.</summary>
    Task<AuditJob?> JobAsync(string id, CancellationToken ct);

    /// <summary>
    /// The newest audit of the repository when <paramref name="pullRequest"/> is null, otherwise the newest review of that pull
    /// request, whatever its status; null when none was started.
    /// </summary>
    Task<AuditJob?> LatestJobAsync(string owner, string repo, int? pullRequest, CancellationToken ct);

    /// <summary>The job's batches in order.</summary>
    Task<IReadOnlyList<AuditBatch>> BatchesAsync(string jobId, CancellationToken ct);

    /// <summary>The job's first pending batch, null when none is, and how many batches the job has.</summary>
    Task<(AuditBatch? Next, int Count)> NextBatchAsync(string jobId, CancellationToken ct);

    /// <summary>The job's first <paramref name="count"/> pending batches in their order, each a distinct batch; empty when none is pending.</summary>
    Task<IReadOnlyList<AuditBatch>> PendingBatchesAsync(string jobId, int count, CancellationToken ct);

    /// <summary>The job's findings in the order they were added.</summary>
    Task<IReadOnlyList<Finding>> FindingsAsync(string jobId, CancellationToken ct);

    /// <summary>The state and recorded reason of each of the job's findings whose state is no longer <c>open</c>, by id.</summary>
    Task<IReadOnlyDictionary<string, (string State, string? Reason)>> FindingStatesAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Records what Scout's second reading answered for each finding still <c>open</c>: its state and, when refuted or
    /// uncertain, the reason; a finding whose state is no longer <c>open</c> keeps it.
    /// </summary>
    Task RecordOutcomesAsync(string jobId, IReadOnlyDictionary<string, ContestOutcome> outcomes, CancellationToken ct);

    /// <summary>Record when the batch started.</summary>
    Task BeginBatchAsync(string jobId, int order, CancellationToken ct);

    /// <summary>
    /// Mark the batch done with the files read, and add the findings the job does not hold yet by path and title, whatever
    /// their case, in one transaction, with <paramref name="leftOut"/>, the sentence on the findings the batch left out;
    /// returns the findings added, each with the id it is kept under.
    /// </summary>
    Task<IReadOnlyList<Finding>> CompleteBatchAsync(
        string jobId, int order, IReadOnlyList<string> filesRead, IReadOnlyList<Finding> findings, string? leftOut, CancellationToken ct);

    /// <summary>Mark the batch not covered, with the files read before it failed and the reason.</summary>
    Task NotCoveredAsync(string jobId, int order, IReadOnlyList<string> filesRead, string reason, CancellationToken ct);

    /// <summary>
    /// Stop the repository's running or paused audit when <paramref name="pullRequest"/> is null, otherwise the review of that
    /// pull request; false when none is. Writes the status row and nothing else.
    /// </summary>
    Task<bool> StopAsync(string owner, string repo, int? pullRequest, CancellationToken ct);

    /// <summary>
    /// Pause a running job with the reason, and the worker leaves it until it is resumed; false, and nothing written, when the
    /// job is not running, so a job already paused keeps the reason it was paused with.
    /// </summary>
    Task<bool> PauseAsync(string jobId, string reason, CancellationToken ct);

    /// <summary>Resume a paused job from its first batch not done; when <paramref name="origin"/> is given, later posts go there.</summary>
    Task ResumeAsync(string jobId, string? origin, CancellationToken ct);

    /// <summary>Mark a running job done; false, and nothing written, when the job is no longer running, as after a pause or a stop.</summary>
    Task<bool> FinishAsync(string jobId, CancellationToken ct);

    /// <summary>The checks a review ran before it started, as they were kept with it; null for an audit or a job with none kept.</summary>
    Task<ReviewPreflight?> PreflightAsync(string jobId, CancellationToken ct);

    /// <summary>The inputs a review's batches are handed, as they were kept with it; null for an audit, a job with none kept, or a kept value that cannot be read.</summary>
    Task<ReviewInputs?> InputsAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// The clock time during which any of the job's batches ran since it last resumed, batches that ran at once counted
    /// once, each from its start to its end; a batch not begun, or begun and
    /// not ended, as one a pause or a stop left, adds nothing, so a job's paused time never counts, and a resume starts the
    /// count again.
    /// </summary>
    Task<TimeSpan> RunningTimeAsync(string jobId, CancellationToken ct);

    /// <summary>Keeps the numbers of one model turn of the job, stamped with its start; names and numbers only.</summary>
    Task AddTurnAsync(string jobId, AuditTurn turn, CancellationToken ct);

    /// <summary>The job's turns totalled per reading, in the order each reading first ran.</summary>
    Task<IReadOnlyList<ReadingTotals>> TurnTotalsAsync(string jobId, CancellationToken ct);

    /// <summary>Writes the Scout version and the provider and chat model names that run the job now, when a worker starts or resumes it.</summary>
    Task StampRunAsync(string jobId, string? version, string? chatModel, CancellationToken ct);
}

/// <summary>
/// The numbers of one model turn of a job: where it ran (the batch's order, null for the second reading; the reading; the
/// second reading's group), its attempt and whether it was the extra request for findings Scout could not place, how long
/// it took and how long its request was, how many times it called each function, what it presented, placed and left out,
/// its failure, for the second reading how many findings held, were refuted and stayed uncertain, and when its last
/// function call ended, in milliseconds from its start (null when it called none), which tells a turn that waited on the
/// model after its reads from one whose reads were slow.
/// </summary>
internal sealed record AuditTurn(
    int? Batch, string Reading, int? Group, int Attempt, bool Extra, long ElapsedMs, int RequestLength, IReadOnlyDictionary<string, int> Calls,
    int Presented, int Placed, int LeftOut, string? Failure, int? Held = null, int? Refuted = null, int? Uncertain = null, long? LastCallMs = null);

/// <summary>
/// One reading's turns of a job, totalled: how many, how long in all, how many function calls, the findings presented,
/// placed and left out, the turns that failed, and the batches (or groups) where no turn of the reading completed.
/// </summary>
internal sealed record ReadingTotals(string Reading, int Turns, long ElapsedMs, int Calls, int Presented, int Placed, int LeftOut, int Failures, int NotCompleted);
