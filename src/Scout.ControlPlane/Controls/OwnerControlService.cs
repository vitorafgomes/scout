using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scout.ControlPlane.Actions;

namespace Scout.ControlPlane.Controls;

/// <summary>What the desktop is told about a control it asked for: whether it was queued, why, and its id when it was.</summary>
public sealed record OwnerControlAnswer(bool Accepted, string Reason, string? ControlId = null);

/// <summary>
/// The owner's controls on running jobs, from a click in Scout Desktop to the worker that applies them. A control is
/// checked before it is queued: an audit or a test run, a repository named by the audit's rule, a test run's pull request
/// number, and an action that applies to the kind (a test run can only be stopped). The worker takes each control once; a
/// control older than <see cref="MaxAge"/> when the worker asks is never handed out and is recorded as not applied. The
/// worker settles each control once, and the write log keeps it as the owner's, under the GitHub feature, with the
/// worker's sentence as its outcome and the control's id; one it took and did not settle within <see cref="MaxAge"/> is
/// recorded as <see cref="NotConfirmed"/>, so no click the worker took is missing from the log.
/// </summary>
public sealed partial class OwnerControlService(IOwnerControlQueue queue, IWriteLog writes, ILogger<OwnerControlService> logger, TimeProvider? time = null)
    : IDisposable
{
    /// <summary>The feature a control's record is kept under.</summary>
    public const string FeatureId = "github";

    /// <summary>The age past which a control is no longer applied: a click the worker could not take in time may no longer mean what the owner meant.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    /// <summary>The most characters of a settled control's outcome that are kept.</summary>
    public const int MaxOutcomeLength = 2_000;

    /// <summary>The answer to a control that was queued.</summary>
    public const string Queued = "Queued; Scout applies it within a few seconds.";

    /// <summary>Why a control of another kind is refused.</summary>
    public const string UnknownKind = "Scout controls audits and test runs only.";

    /// <summary>Why a control with another action is refused.</summary>
    public const string UnknownAction = "The action must be pause, continue or stop.";

    /// <summary>Why a repository that does not follow the audit's naming rule is refused.</summary>
    public const string InvalidRepository =
        "The repository must be given as owner and repo, each made of letters, digits, dots, hyphens or underscores.";

    /// <summary>Why a pause or continue of a test run is refused.</summary>
    public const string TestsStopOnly = "A test run can only be stopped.";

    /// <summary>Why a test run without its pull request number is refused.</summary>
    public const string TestsNeedNumber = "A test run is named by its pull request number, above zero.";

    /// <summary>Why an audit control that names a pull request is refused.</summary>
    public const string AuditHasNoNumber = "An audit is named by its repository alone, without a pull request number.";

    /// <summary>The outcome recorded for a control that waited longer than <see cref="MaxAge"/>.</summary>
    public const string Expired = "Not applied: the click waited more than two minutes for Scout, so it was dropped; click again if it still applies.";

    /// <summary>The outcome recorded for a control the worker took and did not settle within <see cref="MaxAge"/>: it may or may not have been applied.</summary>
    public const string NotConfirmed = "Not confirmed: Scout took this click but did not say how it ended; look at the job before clicking again.";

    private readonly IOwnerControlQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly IWriteLog _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    private readonly ILogger<OwnerControlService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _settleGate = new(1, 1);

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex Name();

    /// <summary>Check the owner's click and queue it for the worker, or say why not; a store that cannot keep it queues nothing.</summary>
    public async Task<OwnerControlAnswer> RequestAsync(string? kind, string? owner, string? repo, int? number, string? action, CancellationToken ct)
    {
        if (Refusal(kind, owner, repo, number, action) is { } refused)
        {
            return new OwnerControlAnswer(false, refused);
        }

        var control = new OwnerControl(Guid.NewGuid().ToString("N"), kind!, owner!, repo!, kind == OwnerControlKinds.Tests ? number : null, action!, _time.GetUtcNow());
        try
        {
            await SweepAsync(await _queue.UnsettledAsync(_time.GetUtcNow() - MaxAge, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The controls taken and never settled could not be read ({ExceptionType}); the new click is queued anyway.", ex.GetType().Name);
        }

        try
        {
            await _queue.EnqueueAsync(control, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("An owner control could not be queued ({ExceptionType}).", ex.GetType().Name);
            return new OwnerControlAnswer(false, $"Scout could not keep this control ({ex.GetType().Name}); nothing was queued, try again.");
        }

        return new OwnerControlAnswer(true, Queued, control.Id);
    }

    /// <summary>
    /// The oldest control waiting, taken for the worker, in one read of the queue when none waits; the ones older than
    /// <see cref="MaxAge"/> are recorded as expired and skipped, and the ones taken earlier and never settled are recorded as
    /// not confirmed.
    /// </summary>
    public async Task<OwnerControl?> TakeNextAsync(CancellationToken ct)
    {
        while (true)
        {
            var now = _time.GetUtcNow();
            var (taken, lapsed) = await _queue.TakeNextAsync(now, now - MaxAge, ct).ConfigureAwait(false);
            await SweepAsync(lapsed, ct).ConfigureAwait(false);
            if (taken is null || taken.TakenAt - taken.RequestedAt <= MaxAge)
            {
                return taken;
            }

            await RecordAsync(taken.Id, OwnerControlStatuses.Expired, Expired, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Record how the taken control <paramref name="id"/> ended, with the worker's <paramref name="status"/>
    /// (<see cref="OwnerControlStatuses.Applied"/>, <see cref="OwnerControlStatuses.NotApplied"/> or
    /// <see cref="OwnerControlStatuses.NotConfirmed"/>) and sentence: true when it is recorded now, false when no taken
    /// control waits for its record under that id.
    /// </summary>
    /// <exception cref="ArgumentException">The id or the outcome is empty, or the status is not one the worker gives.</exception>
    public Task<bool> SettleAsync(string id, string status, string outcome, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        if (status is not (OwnerControlStatuses.Applied or OwnerControlStatuses.NotApplied or OwnerControlStatuses.NotConfirmed))
        {
            throw new ArgumentException("The status must be applied, not applied or not confirmed.", nameof(status));
        }

        return RecordAsync(id, status, outcome, ct);
    }

    /// <summary>
    /// Under one gate: the record is appended first and the control marked settled only after, so a control is never
    /// settled without its record; a record the log already holds for the control's id, from an attempt whose mark failed,
    /// is not appended again.
    /// </summary>
    private async Task<bool> RecordAsync(string id, string status, string outcome, CancellationToken ct)
    {
        var kept = outcome.Trim();
        kept = kept.Length <= MaxOutcomeLength ? kept : kept[..MaxOutcomeLength];
        await _settleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var control = (await _queue.UnsettledAsync(DateTimeOffset.MaxValue, ct).ConfigureAwait(false))
                .FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
            if (control is null)
            {
                return false;
            }

            var recorded = (await _writes.ListSinceAsync(control.RequestedAt, ct).ConfigureAwait(false))
                .Any(e => string.Equals(e.ActionId, id, StringComparison.Ordinal) && e.FeatureId == FeatureId);
            if (!recorded)
            {
                await _writes.AppendAsync(
                        new WriteLogEntry(_time.GetUtcNow(), FeatureId, LogAction(control), control.Target, kept, "owner", control.Id, ControlStatus: status),
                        ct)
                    .ConfigureAwait(false);
            }

            await _queue.SettleAsync(id, _time.GetUtcNow(), ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _settleGate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _settleGate.Dispose();

    /// <summary>The write log's action for a control: <c>Pause audit</c>, <c>Continue audit</c>, <c>Stop audit</c> or <c>Stop tests</c>.</summary>
    public static string LogAction(OwnerControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var verb = control.Action switch
        {
            OwnerControlActions.Pause => "Pause",
            OwnerControlActions.Continue => "Continue",
            _ => "Stop",
        };
        return $"{verb} {(control.Kind == OwnerControlKinds.Tests ? "tests" : "audit")}";
    }

    /// <summary>
    /// Record as not confirmed every control in <paramref name="lapsed"/>, taken more than <see cref="MaxAge"/> ago and
    /// never settled, so none is missing from the write log; a record that fails is logged and tried on the next sweep,
    /// and never keeps a new click from being queued or taken.
    /// </summary>
    private async Task SweepAsync(IReadOnlyList<OwnerControl> lapsed, CancellationToken ct)
    {
        foreach (var control in lapsed)
        {
            try
            {
                await RecordAsync(control.Id, OwnerControlStatuses.NotConfirmed, NotConfirmed, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("An owner control taken and never settled could not be recorded yet ({ExceptionType}).", ex.GetType().Name);
            }
        }
    }

    private static string? Refusal(string? kind, string? owner, string? repo, int? number, string? action)
    {
        if (kind is not (OwnerControlKinds.Audit or OwnerControlKinds.Tests))
        {
            return UnknownKind;
        }

        if (action is not (OwnerControlActions.Pause or OwnerControlActions.Continue or OwnerControlActions.Stop))
        {
            return UnknownAction;
        }

        if (owner is null or "." or ".." || repo is null or "." or ".." || !Name().IsMatch(owner) || !Name().IsMatch(repo))
        {
            return InvalidRepository;
        }

        return kind switch
        {
            OwnerControlKinds.Tests when action != OwnerControlActions.Stop => TestsStopOnly,
            OwnerControlKinds.Tests when number is not > 0 => TestsNeedNumber,
            OwnerControlKinds.Audit when number is not null => AuditHasNoNumber,
            _ => null,
        };
    }
}
