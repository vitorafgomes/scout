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
/// why it paused, its kind, and the pull request a review is of.
/// </summary>
internal sealed record AuditJob(
    string Id, string Owner, string Repo, string Commit, string? Origin, string Status, string? PausedReason = null, string Kind = AuditKind.Audit, int? PullRequest = null);

/// <summary>
/// One batch of a job: its order, its area and files, whether it is pending, done or not covered, the files the model read,
/// why it was not covered, and the files longer than a read returns.
/// </summary>
internal sealed record AuditBatch(
    int Order, string Area, IReadOnlyList<string> Files, string Status, IReadOnlyList<string> FilesRead, string? Failure, IReadOnlyList<string>? Partial = null);

/// <summary>One finding of a job, in the fields the desktop's cards carry.</summary>
internal sealed record AuditFinding(string Id, string Severity, string Title, string Path, int Line, string? Context, string Why, string Fix, string? Rule);

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
    /// it a failure, and the checks a review ran before it started in the same save; answers the job's id. A job with
    /// <paramref name="pullRequest"/> is a review of that pull request, one without it an audit, which keeps no checks.
    /// </summary>
    Task<string> CreateJobAsync(
        string owner, string repo, int? pullRequest, string commit, string? origin, IReadOnlyList<AuditBatchPlan> batches, ReviewPreflight? preflight, CancellationToken ct);

    /// <summary>The running jobs, oldest first.</summary>
    Task<IReadOnlyList<AuditJob>> RunningJobsAsync(CancellationToken ct);

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

    /// <summary>The job's findings in the order they were added.</summary>
    Task<IReadOnlyList<AuditFinding>> FindingsAsync(string jobId, CancellationToken ct);

    /// <summary>Record when the batch started.</summary>
    Task BeginBatchAsync(string jobId, int order, CancellationToken ct);

    /// <summary>
    /// Mark the batch done with the files read, and add the findings the job does not hold yet by path and title, whatever
    /// their case, in one transaction; returns the findings added, each with the id it is kept under.
    /// </summary>
    Task<IReadOnlyList<AuditFinding>> CompleteBatchAsync(
        string jobId, int order, IReadOnlyList<string> filesRead, IReadOnlyList<AuditFinding> findings, CancellationToken ct);

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
}
