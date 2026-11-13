using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Scout.Audit;

/// <summary>
/// Starts, pauses, stops and reports on the whole-repository audit Scout runs as a job, in sentences the model relays to the
/// owner. Starting takes the repository's snapshot through the GitHub plugin's declared snapshot write, plans the
/// batches and keeps the job under Scout's data folder, or resumes a paused job from its first batch not done; pausing and
/// stopping write the job's status and nothing else; the status is read from the job's rows. None of the four makes a call
/// to GitHub of its own.
/// </summary>
public interface IRepositoryAudits
{
    /// <summary>Start an audit of the repository and answer at once with its plan, resume its paused audit, or say why neither happened.</summary>
    Task<string> StartAsync(string owner, string repo, CancellationToken ct);

    /// <summary>
    /// Pause the repository's running audit; an area in progress finishes first and its findings stay; a start resumes it from the
    /// first batch not done. An audit already paused keeps the reason it was paused with.
    /// </summary>
    Task<string> PauseAsync(string owner, string repo, CancellationToken ct);

    /// <summary>
    /// Continue the repository's paused audit from its first area not done, in the conversation the job already holds when
    /// the caller has none, as from Scout Desktop; a running audit, or none to continue, is said in a sentence, and no new
    /// audit is ever started.
    /// </summary>
    Task<string> ContinueAsync(string owner, string repo, CancellationToken ct);

    /// <summary>
    /// Apply the owner's <paramref name="action"/>, <c>pause</c>, <c>continue</c> or <c>stop</c>, as <see cref="PauseAsync"/>,
    /// <see cref="ContinueAsync"/> and <see cref="StopAsync"/> do, and say whether it changed the job: false for a click on a
    /// job it does not apply to, which changes nothing.
    /// </summary>
    Task<AuditControlAnswer> ControlAsync(string owner, string repo, string action, CancellationToken ct);

    /// <summary>Stop the repository's running or paused audit; an area in progress finishes first and the findings so far stay.</summary>
    Task<string> StopAsync(string owner, string repo, CancellationToken ct);

    /// <summary>How the repository's latest audit is going, from its rows.</summary>
    Task<string> StatusAsync(string owner, string repo, CancellationToken ct);
}

/// <summary>What a control on an audit did: whether it changed the job, and the sentence the owner reads.</summary>
public sealed record AuditControlAnswer(bool Applied, string Sentence);

/// <summary>
/// The <see cref="IRepositoryAudits"/> over the audit store: the snapshot is taken by invoking the GitHub plugin's
/// <c>snapshot_repository</c> through the kernel, so it passes the write gate like any declared write, then found through
/// <see cref="IAuditSnapshots"/> by the short commit its answer names; a review is checked against
/// <see cref="IReviewEnvironment"/> before it starts. The kernel is resolved on use, because the plugin that calls this
/// service is built into that kernel.
/// </summary>
internal sealed partial class RepositoryAudits(
    IAuditJobStore store,
    AuditSignal signal,
    AuditOptions options,
    IAuditSnapshots snapshots,
    IReviewEnvironment environment,
    Func<Kernel> kernel,
    Func<string?> origin,
    ILogger<RepositoryAudits> logger) : IRepositoryAudits
{
    internal const string ActionsPlugin = "GitHubActions";
    internal const string SnapshotRepository = "snapshot_repository";

    internal const string InvalidRepository =
        "The repository must be given as owner and repo, each made of letters, digits, dots, hyphens or underscores.";

    internal const string NoGitHub = "GitHub is not connected, so Scout cannot take the snapshot an audit reads.";

    /// <summary>The reason a job the owner paused is kept under, read in the status as "is paused: you paused it".</summary>
    internal const string PausedByOwner = "you paused it";

    /// <summary>The control that pauses a running audit.</summary>
    internal const string Pause = "pause";

    /// <summary>The control that continues a paused audit.</summary>
    internal const string Continue = "continue";

    /// <summary>The control that stops a running or paused audit.</summary>
    internal const string Stop = "stop";

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex Name();

    [GeneratedRegex(@"\(([0-9a-fA-F]{7,40})\)")]
    private static partial Regex NamedCommit();

    internal static string Unreadable(string job, string reason) =>
        $"The {job} did not start: Scout could not read the snapshot it just took ({reason}).";

    internal static string Plan(string owner, string repo, int areas, int files) =>
        string.Create(CultureInfo.InvariantCulture, $"Auditing {owner}/{repo}: {areas} areas, {files} files; the first batch is running. Findings arrive here as each area is done.");

    public async Task<string> StartAsync(string owner, string repo, CancellationToken ct)
    {
        if (!IsValid(owner, repo))
        {
            return InvalidRepository;
        }

        try
        {
            var latest = await store.LatestJobAsync(owner, repo, null, ct).ConfigureAwait(false);
            if (latest is { Status: AuditStatus.Running })
            {
                signal.Wake();
                return $"An audit of {owner}/{repo} is already running. {await ProgressAsync(latest, ct).ConfigureAwait(false)}";
            }

            if (latest is { Status: AuditStatus.Paused })
            {
                return await ResumeAsync(latest, ct).ConfigureAwait(false);
            }

            var (taken, refusal) = await SnapshotAsync(owner, repo, string.Empty, $"audit of {owner}/{repo}", ct).ConfigureAwait(false);
            if (taken is null)
            {
                return refusal;
            }

            var plan = AuditPlanner.Plan(taken.Root, options.MaxBatchFiles, options.MaxBatchCharacters);
            if (plan.Count == 0)
            {
                return $"Nothing to audit in {owner}/{repo}: the snapshot holds no file the audit reads.";
            }

            await store.CreateJobAsync(owner, repo, null, taken.Commit, origin(), plan, null, ct).ConfigureAwait(false);
            signal.Wake();
            return Plan(owner, repo, plan.Count, plan.Sum(b => b.Files.Count));
        }
        catch (AuditStoreException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The audit of a repository could not start ({ExceptionType}).", ex.GetType().Name);
            return $"Scout could not take the snapshot of {owner}/{repo} ({ex.GetType().Name}).";
        }
    }

    /// <summary>
    /// Takes the repository's snapshot at <paramref name="reference"/>, the default branch when empty, by invoking
    /// <c>snapshot_repository</c> through the kernel, so the write gate is asked, and finds it by the short commit the answer
    /// names; when there is none to plan from, the sentence the owner reads instead, naming <paramref name="job"/>.
    /// </summary>
    private async Task<(AuditSnapshot? Taken, string Refusal)> SnapshotAsync(string owner, string repo, string reference, string job, CancellationToken ct)
    {
        var shared = kernel();
        if (!shared.Plugins.TryGetFunction(ActionsPlugin, SnapshotRepository, out var snapshot))
        {
            return (null, NoGitHub);
        }

        var answer = (await shared.InvokeAsync(snapshot, new KernelArguments { ["owner"] = owner, ["repo"] = repo, ["reference"] = reference }, ct)
            .ConfigureAwait(false)).GetValue<string>() ?? string.Empty;
        if (NamedCommit().Match(answer) is not { Success: true } named)
        {
            return (null, answer.Length == 0 ? $"Scout could not take the snapshot of {owner}/{repo}." : answer);
        }

        AuditSnapshot? found;
        try
        {
            found = snapshots.Find(owner, repo, named.Groups[1].Value);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Scout could not read the snapshot it just took for a job ({ExceptionType}).", ex.GetType().Name);
            return (null, Unreadable(job, ex.GetType().Name));
        }

        if (found is null)
        {
            logger.LogWarning("Scout could not find the snapshot it just took for a job.");
            return (null, Unreadable(job, "not found"));
        }

        return (found, string.Empty);
    }

    public async Task<string> ContinueAsync(string owner, string repo, CancellationToken ct) =>
        (await ControlAsync(owner, repo, Continue, ct).ConfigureAwait(false)).Sentence;

    public async Task<string> PauseAsync(string owner, string repo, CancellationToken ct) =>
        (await ControlAsync(owner, repo, Pause, ct).ConfigureAwait(false)).Sentence;

    public async Task<string> StopAsync(string owner, string repo, CancellationToken ct) =>
        (await ControlAsync(owner, repo, Stop, ct).ConfigureAwait(false)).Sentence;

    public async Task<AuditControlAnswer> ControlAsync(string owner, string repo, string action, CancellationToken ct)
    {
        if (!IsValid(owner, repo))
        {
            return new AuditControlAnswer(false, InvalidRepository);
        }

        try
        {
            return action switch
            {
                Pause => await PauseJobAsync(owner, repo, ct).ConfigureAwait(false),
                Continue => await ContinueJobAsync(owner, repo, ct).ConfigureAwait(false),
                Stop => await StopJobAsync(owner, repo, ct).ConfigureAwait(false),
                _ => new AuditControlAnswer(false, $"Scout does not {action} an audit."),
            };
        }
        catch (AuditStoreException ex)
        {
            return new AuditControlAnswer(false, ex.Message);
        }
    }

    private async Task<AuditControlAnswer> ContinueJobAsync(string owner, string repo, CancellationToken ct) =>
        await store.LatestJobAsync(owner, repo, null, ct).ConfigureAwait(false) switch
        {
            { Status: AuditStatus.Paused } paused => new AuditControlAnswer(true, await ResumeAsync(paused, ct).ConfigureAwait(false)),
            { Status: AuditStatus.Running } running => new AuditControlAnswer(false, $"The audit of {owner}/{repo} is already running. {await ProgressAsync(running, ct).ConfigureAwait(false)}"),
            _ => new AuditControlAnswer(false, $"No paused audit of {owner}/{repo} to continue."),
        };

    private async Task<AuditControlAnswer> PauseJobAsync(string owner, string repo, CancellationToken ct)
    {
        var job = await store.LatestJobAsync(owner, repo, null, ct).ConfigureAwait(false);
        var resume = $"Ask Scout to continue the audit of {owner}/{repo} to resume it from the first area not done.";
        if (job is { Status: AuditStatus.Running } && await store.PauseAsync(job.Id, PausedByOwner, ct).ConfigureAwait(false))
        {
            return new AuditControlAnswer(true, $"Paused the audit of {owner}/{repo}; an area in progress finishes first. The findings so far stay on the cards. {await ProgressAsync(job, ct).ConfigureAwait(false)} {resume}");
        }

        job = job is null ? null : await store.JobAsync(job.Id, ct).ConfigureAwait(false);
        return new AuditControlAnswer(false, job is { Status: AuditStatus.Paused }
            ? $"The audit of {owner}/{repo} is already paused{(job.PausedReason is { } reason ? $": {reason}" : string.Empty)}. {await ProgressAsync(job, ct).ConfigureAwait(false)} {resume}"
            : $"No audit of {owner}/{repo} is running.");
    }

    private async Task<AuditControlAnswer> StopJobAsync(string owner, string repo, CancellationToken ct)
    {
        if (!await store.StopAsync(owner, repo, null, ct).ConfigureAwait(false))
        {
            return new AuditControlAnswer(false, $"No audit of {owner}/{repo} is running.");
        }

        var job = await store.LatestJobAsync(owner, repo, null, ct).ConfigureAwait(false);
        return new AuditControlAnswer(true, $"Stopped the audit of {owner}/{repo}; an area in progress finishes first. The findings so far stay on the cards. {await ProgressAsync(job!, ct).ConfigureAwait(false)}");
    }

    public async Task<string> StatusAsync(string owner, string repo, CancellationToken ct)
    {
        if (!IsValid(owner, repo))
        {
            return InvalidRepository;
        }

        try
        {
            if (await store.LatestJobAsync(owner, repo, null, ct).ConfigureAwait(false) is not { } job)
            {
                return $"No audit of {owner}/{repo} has been started.";
            }

            var state = job is { Status: AuditStatus.Paused, PausedReason: { } reason } ? $"{job.Status}: {reason}" : job.Status;
            return $"The audit of {owner}/{repo} at {job.Commit[..Math.Min(7, job.Commit.Length)]} is {state}. {await ProgressAsync(job, ct).ConfigureAwait(false)}";
        }
        catch (AuditStoreException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> ResumeAsync(AuditJob paused, CancellationToken ct)
    {
        await store.ResumeAsync(paused.Id, origin(), ct).ConfigureAwait(false);
        signal.Wake();
        return $"Resuming the audit of {paused.Owner}/{paused.Repo} from the first area not done. {await ProgressAsync(paused, ct).ConfigureAwait(false)} Findings arrive here as each area is done.";
    }

    private async Task<string> ProgressAsync(AuditJob job, CancellationToken ct)
    {
        var batches = await store.BatchesAsync(job.Id, ct).ConfigureAwait(false);
        var findings = await store.FindingsAsync(job.Id, ct).ConfigureAwait(false);
        var done = batches.Count(b => b.Status == AuditStatus.Done);
        var missed = batches.Count(b => b.Status == AuditStatus.NotCovered);
        var read = batches.SelectMany(b => b.FilesRead).Distinct(StringComparer.Ordinal).Count();
        var unit = job.Kind == AuditKind.Review ? "batches" : "areas";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{done} of {batches.Count} {unit} done, {missed} not covered, {findings.Count} findings, {read} files read.");
    }

    private static bool IsValid(string owner, string repo) =>
        owner is not (null or "." or "..") && repo is not (null or "." or "..") && Name().IsMatch(owner) && Name().IsMatch(repo);
}
