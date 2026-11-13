using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.WriteGate;

/// <summary>What the gate answers a write request.</summary>
public enum WriteDecisionKind
{
    /// <summary>A reversible write under the owner's grant; it may run now.</summary>
    Allowed,

    /// <summary>An irreversible write under the grant; it waits for the owner's approval.</summary>
    Queued,

    /// <summary>Undeclared, a read, or not granted; it must not run.</summary>
    Refused,
}

/// <summary>
/// The gate's answer; <c>ActionId</c> is the queued action's id when the write was queued, or the id of its
/// <see cref="WriteGateService.StartedOutcome"/> entry when the worker's request allowed it, and null otherwise.
/// </summary>
public sealed record WriteDecision(WriteDecisionKind Kind, string Reason, string? ActionId = null);

/// <summary>Thrown when a write cannot be recorded because it was not permitted.</summary>
/// <remarks>Build the exception with the reason the record was refused.</remarks>
public sealed class WriteNotRecordableException(string message) : Exception(message)
{
}

/// <summary>
/// The only path through which the worker asks to write and records what it wrote.
/// A write runs only when its feature declares the scope as a write and the owner
/// granted the feature; an irreversible one is queued for the owner instead. The
/// grant is read from the state store on every request, and once more just before a worker sends a claimed action. The
/// gate never grants and never approves; it rejects, and logs, a claimed action whose grant is gone.
/// </summary>
/// <remarks>Build the gate over the catalog, the feature state, the queue, the log and the clock an approval's age is read from.</remarks>
public sealed class WriteGateService(
    IFeatureCatalog catalog,
    IFeatureStateStore states,
    IActionQueue actions,
    IWriteLog writes,
    TimeProvider? time = null)
{
    /// <summary>
    /// Outcome of the write-log entry that records the owner's approval of an action made in the desktop; it is not
    /// the record of the write, so it never counts as one.
    /// </summary>
    public const string ApprovedOutcome = "approved";

    /// <summary>Why an action that waited for the owner is rejected when its feature's write grant is cleared.</summary>
    public const string WritesRevoked = "writes were revoked";

    /// <summary>Why an action a worker claimed before the current grant, and never finished, is rejected once its claim lapses.</summary>
    public const string NeverFinished = "Scout was running it when writes were revoked and it never finished; ask again";

    /// <summary>Why an approved action is rejected, when a worker asks for it, while the write log holds no approval of it.</summary>
    public const string ApprovalNotInLog = "its approval is not in the write log; approve it again";

    /// <summary>
    /// How long after the owner approved an action its approval may still be on its way to the write log: the approval is
    /// saved in the queue first and logged right after, in the same call. Within it a claim leaves an action with no
    /// approval in the log approved and hands it out to no one; from then on it rejects the action.
    /// </summary>
    public static readonly TimeSpan ApprovalLogWindow = TimeSpan.FromMinutes(1);

    /// <summary>Outcome of an action a worker claimed and never recorded, once its claim lapses: whether it was sent is unknown.</summary>
    public const string NotConfirmedOutcome = "failed: not confirmed";

    /// <summary>What the owner reads on an action recorded as <see cref="NotConfirmedOutcome"/>; <paramref name="where"/> is the feature's name.</summary>
    public static string NotConfirmed(string where) => NotConfirmedBefore + where + NotConfirmedAfter;

    /// <summary>
    /// Whether <paramref name="reason"/> is <see cref="NotConfirmed"/> for some feature, as the claim path writes it on an
    /// action whose claim lapsed with no record: that action may already have been sent.
    /// </summary>
    internal static bool IsNotConfirmed(string? reason) =>
        reason is not null
        && reason.Length > NotConfirmedBefore.Length + NotConfirmedAfter.Length
        && reason.StartsWith(NotConfirmedBefore, StringComparison.Ordinal)
        && reason.EndsWith(NotConfirmedAfter, StringComparison.Ordinal);

    private const string NotConfirmedBefore = "Scout stopped before it recorded this, so it may already be on ";
    private const string NotConfirmedAfter = ": look there before asking for it again.";

    private const string Waiting = "Waiting for the owner's approval on the Review screen of Scout Desktop.";

    /// <summary>Why an irreversible write whose payload is too large is refused.</summary>
    public const string PayloadTooLarge = "This write carries over 200,000 characters, too much to keep for the owner's approval; send less.";

    private readonly IFeatureCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IFeatureStateStore _states = states ?? throw new ArgumentNullException(nameof(states));
    private readonly IActionQueue _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    private readonly IWriteLog _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The approval queue the gate records against.</summary>
    internal IActionQueue Actions => _actions;

    /// <summary>When the owner last granted <paramref name="featureId"/>'s writes; null while not granted.</summary>
    internal async Task<DateTimeOffset?> GrantedAtAsync(string featureId, CancellationToken ct) =>
        (await _states.GetAsync(featureId, ct).ConfigureAwait(false)).GrantedAt;

    /// <summary>How long a worker's claim on an approved action holds before the action counts as never recorded.</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(30);

    /// <summary>
    /// For a worker that runs a feature's approved actions itself: claim the oldest approved action of
    /// <paramref name="featureId"/> that was requested under the current grant, or an executing one whose claim lapsed.
    /// Null when the feature holds no grant, or nothing waits. An action requested before the owner granted, or
    /// re-granted, the feature is never handed out: the grant it ran under is gone. An executing one of those whose
    /// claim lapsed, or any executing one whose claim lapsed while the feature holds no grant at all, is rejected and logged
    /// first, so it does not stay running forever: with <see cref="NeverFinished"/>, or, for a feature that declares
    /// <see cref="FeatureMetadata.UnrecordedClaimMayHaveBeenSent"/>, with <see cref="NotConfirmed"/>, since it may have been
    /// sent; one whose write is already in the write log is marked executed instead.
    /// </summary>
    public async Task<PendingAction?> ClaimNextApprovedAsync(string featureId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        var grantedAt = await GrantedAtAsync(featureId, ct).ConfigureAwait(false);
        var lapsed = _time.GetUtcNow() - ClaimLease;
        var feature = _catalog.Find(featureId);
        var neverRecorded = feature?.UnrecordedClaimMayHaveBeenSent == true ? NotConfirmed(feature.DisplayName) : NeverFinished;
        await RejectAndLogAsync(
                featureId, action => action.ClaimedAt <= lapsed && (grantedAt is null || action.RequestedAt < grantedAt), neverRecorded, ct)
            .ConfigureAwait(false);
        if (grantedAt is null)
        {
            return null;
        }

        return await ClaimUnrecordedAsync(featureId, (action, _) => action.RequestedAt >= grantedAt, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Claim the next action of <paramref name="featureId"/> as <see cref="IActionQueue.ClaimNextAsync"/> does, for
    /// <see cref="ClaimLease"/>. An action whose write is already in the write log, as when the mark failed after the
    /// append, already ran: it is marked executed with the logged outcome instead of handed out, and the next one is
    /// claimed. An action whose claim lapsed with no write in the log may or may not have been sent: for a feature that
    /// declares <see cref="FeatureMetadata.UnrecordedClaimMayHaveBeenSent"/> it is recorded as
    /// <see cref="NotConfirmedOutcome"/> with <see cref="NotConfirmed"/>, logged and never handed out again, and the next
    /// one is claimed; for any other feature it is handed out again. The checks run in that order: a write in the log,
    /// then the lapsed claim, then the approval. An action is handed out only when the write log holds the owner's approval of it. One without, approved
    /// less than <see cref="ApprovalLogWindow"/> ago, is returned to the queue as approved and skipped for this call, since
    /// its approval may still be on its way to the log; one approved longer ago is rejected with
    /// <see cref="ApprovalNotInLog"/>, the rejection logged. Either way the next one is claimed. A write log that cannot
    /// be read hands out nothing.
    /// </summary>
    internal async Task<PendingAction?> ClaimUnrecordedAsync(
        string featureId, Func<PendingAction, IReadOnlyList<PendingAction>, bool> eligible, CancellationToken ct)
    {
        var awaitingTheirApproval = new HashSet<string>(StringComparer.Ordinal);
        var claimLapsed = new HashSet<string>(StringComparer.Ordinal);
        var feature = _catalog.Find(featureId);
        bool Take(PendingAction action, IReadOnlyList<PendingAction> open)
        {
            if (awaitingTheirApproval.Contains(action.Id) || !eligible(action, open))
            {
                return false;
            }

            if (action.Status == PendingActionStatus.Executing)
            {
                claimLapsed.Add(action.Id);
            }

            return true;
        }

        while (await _actions.ClaimNextAsync(featureId, ClaimLease, Take, ct).ConfigureAwait(false) is { } claimed)
        {
            var logged = await _writes.ListSinceAsync(claimed.RequestedAt, ct).ConfigureAwait(false);
            if (await SettleRecordedAsync(claimed, logged, ct).ConfigureAwait(false))
            {
                continue;
            }

            if (feature?.UnrecordedClaimMayHaveBeenSent == true && claimLapsed.Contains(claimed.Id))
            {
                try
                {
                    await RecordOnceAsync(claimed, claimed.Target, NotConfirmedOutcome, NotConfirmed(feature.DisplayName), null, ct)
                        .ConfigureAwait(false);
                }
                catch (WriteNotRecordableException)
                {
                }

                continue;
            }

            if (logged.Any(entry => string.Equals(entry.ActionId, claimed.Id, StringComparison.Ordinal) && IsApproval(entry.Outcome)))
            {
                return claimed;
            }

            if (claimed.DecidedAt is { } approvedAt && _time.GetUtcNow() - approvedAt < ApprovalLogWindow)
            {
                await _actions.ReleaseAsync(claimed.Id, ct).ConfigureAwait(false);
                awaitingTheirApproval.Add(claimed.Id);
                continue;
            }

            foreach (var rejected in await _actions
                .RejectExecutingAsync(featureId, action => string.Equals(action.Id, claimed.Id, StringComparison.Ordinal), ApprovalNotInLog, ct)
                .ConfigureAwait(false))
            {
                await _writes.AppendAsync(WriteLogEntry.Rejected(rejected, ApprovalNotInLog), ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<bool> SettleRecordedAsync(PendingAction action, IReadOnlyList<WriteLogEntry> logged, CancellationToken ct)
    {
        var recorded = logged.FirstOrDefault(entry => IsRecordOf(entry, action));
        if (recorded is null)
        {
            return false;
        }

        try
        {
            await _actions.ExecuteAsync(action.Id, recorded.Outcome, null, _ => Task.CompletedTask, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PendingActionNotApprovedException or PendingActionNotFoundException)
        {
        }

        return true;
    }

    /// <summary>
    /// Read the grant once more just before a worker sends the action <paramref name="actionId"/> it claimed: true while the
    /// action is still executing for <paramref name="featureId"/> and was requested under the current grant. When the grant
    /// is gone, or was given again after the request, the action is rejected with <see cref="WritesRevoked"/>, logged, and
    /// the answer is false; an action no longer executing answers false and is left as it is.
    /// </summary>
    public async Task<bool> CheckGrantAsync(string featureId, string actionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        var grantedAt = await GrantedAtAsync(featureId, ct).ConfigureAwait(false);
        var action = await _actions.FindAsync(actionId, ct).ConfigureAwait(false);
        if (action is null
            || !string.Equals(action.FeatureId, featureId, StringComparison.Ordinal)
            || action.Status != PendingActionStatus.Executing)
        {
            return false;
        }

        if (grantedAt is not null && action.RequestedAt >= grantedAt)
        {
            return true;
        }

        await RejectAndLogAsync(featureId, a => string.Equals(a.Id, actionId, StringComparison.Ordinal), WritesRevoked, ct)
            .ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Reject, with <paramref name="reason"/>, the executing actions of <paramref name="featureId"/> that <paramref name="match"/>
    /// accepts, then log each one. One whose write is already in the write log ran: it is marked executed with the logged
    /// outcome first and is not rejected.
    /// </summary>
    internal async Task RejectAndLogAsync(string featureId, Func<PendingAction, bool> match, string reason, CancellationToken ct)
    {
        var claimed = (await _actions.ListOpenAsync(featureId, ct).ConfigureAwait(false))
            .Where(action => action.Status == PendingActionStatus.Executing && match(action))
            .ToList();
        if (claimed.Count == 0)
        {
            return;
        }

        foreach (var executing in claimed)
        {
            await SettleRecordedAsync(executing, await _writes.ListSinceAsync(executing.RequestedAt, ct).ConfigureAwait(false), ct)
                .ConfigureAwait(false);
        }

        foreach (var rejected in await _actions.RejectExecutingAsync(featureId, match, reason, ct).ConfigureAwait(false))
        {
            await _writes.AppendAsync(WriteLogEntry.Rejected(rejected, reason), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Decide whether <paramref name="scope"/> of <paramref name="featureId"/> may be written now.</summary>
    public Task<WriteDecision> RequestAsync(
        string featureId,
        string scope,
        string target,
        string summary,
        CancellationToken ct) => RequestAsync(featureId, scope, target, summary, null, ct);

    /// <summary>
    /// Decide whether <paramref name="scope"/> of <paramref name="featureId"/> may be written now. An irreversible write is
    /// queued with <paramref name="payload"/>, what its run needs after the owner approves it, and refused when the payload
    /// is over <see cref="PendingAction.MaxPayloadLength"/> characters; a write that may run now drops the payload.
    /// </summary>
    public Task<WriteDecision> RequestAsync(
        string featureId,
        string scope,
        string target,
        string summary,
        string? payload,
        CancellationToken ct) => RequestAsync(featureId, scope, target, summary, payload, null, null, ct);

    /// <summary>
    /// As <see cref="RequestAsync(string, string, string, string, string?, CancellationToken)"/>; an irreversible write is
    /// queued in the request <paramref name="groupId"/> on <paramref name="repository"/>, both as the worker's request names
    /// them and never read from the payload.
    /// </summary>
    public async Task<WriteDecision> RequestAsync(
        string featureId,
        string scope,
        string target,
        string summary,
        string? payload,
        string? groupId,
        string? repository,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(summary);

        var row = FindRow(featureId, scope);
        if (row is null)
        {
            return new WriteDecision(WriteDecisionKind.Refused, NotDeclared(featureId, scope));
        }

        if (row.Access == ScopeAccess.Read)
        {
            return new WriteDecision(WriteDecisionKind.Refused, IsARead(scope));
        }

        var state = await _states.GetAsync(featureId, ct).ConfigureAwait(false);
        if (!state.Granted)
        {
            return new WriteDecision(WriteDecisionKind.Refused, NotGranted(featureId));
        }

        if (row.Access == ScopeAccess.Write)
        {
            return new WriteDecision(WriteDecisionKind.Allowed, string.Empty);
        }

        if (payload?.Length > PendingAction.MaxPayloadLength)
        {
            return new WriteDecision(WriteDecisionKind.Refused, PayloadTooLarge);
        }

        var queued = await _actions.EnqueueAsync(featureId, scope, target, summary, payload, groupId, repository, ct).ConfigureAwait(false);
        return new WriteDecision(WriteDecisionKind.Queued, Waiting, queued.Id);
    }

    /// <summary>
    /// Append a write the worker performed to the write log. With <paramref name="actionId"/>
    /// the action must be one the owner approved, or a worker claimed, for this feature, scope and target; an irreversible
    /// scope needs one.
    /// The log entry is appended before the action is marked executed, both under the queue's gate, so an
    /// action is recorded once: a failed append leaves it approved for a retry, and a retry after a failed
    /// mark finds its entry, marks it executed and is refused as already recorded; the lookup reads the log
    /// back only to the first entry older than the action's request. An action a worker claimed and recorded
    /// after the owner revoked the grant already ran: it is logged once with its outcome followed by
    /// "after revoke", marked executed, and the record is refused.
    /// </summary>
    /// <exception cref="WriteNotRecordableException">
    /// The scope is undeclared or a read, the owner has not granted the feature or revoked the grant,
    /// irreversible without an action, or the action is not approved for it or was already recorded.
    /// </exception>
    public Task RecordAsync(
        string featureId,
        string scope,
        string target,
        string outcome,
        string? actionId,
        CancellationToken ct) => RecordAsync(featureId, scope, target, outcome, actionId, null, null, ct);

    /// <summary>As the overload with a signer, when the worker does not know who signed the write.</summary>
    public Task RecordAsync(
        string featureId,
        string scope,
        string target,
        string outcome,
        string? actionId,
        string? result,
        CancellationToken ct) => RecordAsync(featureId, scope, target, outcome, actionId, result, null, ct);

    /// <summary>
    /// As <see cref="RecordAsync(string, string, string, string, string?, CancellationToken)"/>; an approved action recorded
    /// here also keeps <paramref name="outcome"/> and <paramref name="result"/>, what the owner reads of how it ended, and
    /// the log entry names <paramref name="signedAs"/>, the identity that signed the write, when the worker knows it.
    /// </summary>
    public async Task RecordAsync(
        string featureId,
        string scope,
        string target,
        string outcome,
        string? actionId,
        string? result,
        string? signedAs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(outcome);

        var row = FindRow(featureId, scope)
            ?? throw new WriteNotRecordableException(NotDeclared(featureId, scope));
        if (row.Access == ScopeAccess.Read)
        {
            throw new WriteNotRecordableException(IsARead(scope));
        }

        if (row.Access == ScopeAccess.IrreversibleWrite && actionId is null)
        {
            throw new WriteNotRecordableException(
                $"{scope} is irreversible; it is recorded only with an action the owner approved.");
        }

        var granted = (await _states.GetAsync(featureId, ct).ConfigureAwait(false)).Granted;
        if (!granted && row.Access == ScopeAccess.Write)
        {
            throw new WriteNotRecordableException(NotGranted(featureId));
        }

        if (actionId is null)
        {
            await _writes.AppendAsync(
                    new WriteLogEntry(DateTimeOffset.UtcNow, featureId, scope, target, outcome, null, null, signedAs),
                    ct)
                .ConfigureAwait(false);
            return;
        }

        var action = string.IsNullOrWhiteSpace(actionId)
            ? null
            : await _actions.FindAsync(actionId, ct).ConfigureAwait(false);
        if (action is null && row.Access == ScopeAccess.Write && !string.IsNullOrWhiteSpace(actionId))
        {
            await RecordStartedAsync(featureId, scope, target, outcome, actionId, signedAs, ct).ConfigureAwait(false);
            return;
        }

        var sameScope = action is not null
            && string.Equals(action.FeatureId, featureId, StringComparison.Ordinal)
            && string.Equals(action.Action, scope, StringComparison.Ordinal);
        var matches = sameScope && string.Equals(action!.Target, target, StringComparison.Ordinal);
        if (!granted)
        {
            if (matches && action!.Status == PendingActionStatus.Executing)
            {
                await RecordOnceAsync(action, target, $"{outcome} after revoke", result, signedAs, ct).ConfigureAwait(false);
            }

            throw new WriteNotRecordableException(Revoked(featureId));
        }

        if (action is not { Status: PendingActionStatus.Approved or PendingActionStatus.Executing })
        {
            throw new WriteNotRecordableException(NotApproved(actionId));
        }

        if (!sameScope)
        {
            throw new WriteNotRecordableException(
                $"Action '{actionId}' was approved for {action.FeatureId} {action.Action}, not {featureId} {scope}.");
        }

        if (!matches)
        {
            throw new WriteNotRecordableException($"Action '{actionId}' was approved for {action.Target}, not {target}.");
        }

        if (await RecordOnceAsync(action, target, outcome, result, signedAs, ct).ConfigureAwait(false))
        {
            throw new WriteNotRecordableException($"Action '{actionId}' is already recorded in the write log.");
        }
    }

    private async Task<bool> RecordOnceAsync(
        PendingAction action, string target, string outcome, string? result, string? signedAs, CancellationToken ct)
    {
        var alreadyLogged = false;
        try
        {
            await _actions.ExecuteAsync(
                    action.Id,
                    outcome,
                    result,
                    async token =>
                    {
                        alreadyLogged = (await _writes.ListSinceAsync(action.RequestedAt, token).ConfigureAwait(false))
                            .Any(entry => IsRecordOf(entry, action));
                        if (!alreadyLogged)
                        {
                            await _writes.AppendAsync(
                                    new WriteLogEntry(DateTimeOffset.UtcNow, action.FeatureId, action.Action, target, outcome, "owner", action.Id, signedAs)
                                    {
                                        GroupId = action.GroupId,
                                    },
                                    token)
                                .ConfigureAwait(false);
                        }
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PendingActionNotApprovedException or PendingActionNotFoundException)
        {
            throw new WriteNotRecordableException(NotApproved(action.Id));
        }

        return alreadyLogged;
    }

    /// <summary>True for the outcome of the entry that records the owner's approval, with or without a reason.</summary>
    public static bool IsApproval(string outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return string.Equals(outcome, ApprovedOutcome, StringComparison.Ordinal)
            || outcome.StartsWith(ApprovedOutcome + ": ", StringComparison.Ordinal);
    }

    /// <summary>The outcome of the entry that records the owner's approval, followed by <paramref name="reason"/> when there is one.</summary>
    public static string Approval(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? ApprovedOutcome : $"{ApprovedOutcome}: {reason}";

    private static bool IsRecordOf(WriteLogEntry entry, PendingAction action) =>
        string.Equals(entry.ActionId, action.Id, StringComparison.Ordinal) && !IsApproval(entry.Outcome);

    private FeatureScopeRow? FindRow(string featureId, string scope) =>
        string.IsNullOrWhiteSpace(featureId)
            ? null
            : _catalog.Find(featureId)?.ScopeMatrix?
                .FirstOrDefault(row => string.Equals(row.Scope, scope, StringComparison.Ordinal));

    private static string NotDeclared(string featureId, string scope) => $"{scope} is not declared by {featureId}";

    private static string IsARead(string scope) => $"{scope} is a read";

    private static string NotApproved(string actionId) => $"Action '{actionId}' is not approved by the owner.";

    private static string Revoked(string featureId) =>
        $"The owner revoked {featureId}'s write permission before this action ran.";

    private static string NotGranted(string featureId) =>
        $"The owner has not allowed {featureId} to write; allow it on the Features screen of Scout Desktop.";

    /// <summary>
    /// The worker reports that the request <paramref name="groupId"/> ended: its actions are stamped closed and none of
    /// them is decided; the number of actions the group holds.
    /// </summary>
    public Task<int> CloseGroupAsync(string groupId, CancellationToken ct) => _actions.CloseGroupAsync(groupId, ct);

    /// <summary>Outcome of the entry a reversible write gets in the write log before it runs; its record follows under the same id.</summary>
    public const string StartedOutcome = "started";

    /// <summary>
    /// What a <see cref="StartedOutcome"/> entry with no record lists as once <see cref="ClaimLease"/> has passed: the
    /// worker stopped between the start and the record, so the write may have run.
    /// </summary>
    public const string StartedNotRecordedOutcome =
        "not confirmed: Scout stopped before it recorded how this ended, so it may have run";

    /// <summary>
    /// As <see cref="RequestAsync(string, string, string, string, string?, string?, string?, CancellationToken)"/>, for a
    /// worker that runs the write as soon as it is allowed: a reversible write is appended to the write log as
    /// <see cref="StartedOutcome"/> first, and the allowed answer carries that entry's id, which the worker's record names.
    /// When the entry cannot be appended the call fails and the write is not allowed.
    /// </summary>
    public async Task<WriteDecision> RequestToRunAsync(
        string featureId,
        string scope,
        string target,
        string summary,
        string? payload,
        string? groupId,
        string? repository,
        CancellationToken ct)
    {
        var decision = await RequestAsync(featureId, scope, target, summary, payload, groupId, repository, ct).ConfigureAwait(false);
        if (decision.Kind != WriteDecisionKind.Allowed)
        {
            return decision;
        }

        var id = Guid.NewGuid().ToString("N");
        await _writes.AppendAsync(new WriteLogEntry(_time.GetUtcNow(), featureId, scope, target, StartedOutcome, null, id), ct)
            .ConfigureAwait(false);
        return decision with { ActionId = id };
    }

    /// <summary>
    /// The write log as the owner reads it, newest first: a <see cref="StartedOutcome"/> entry whose record, an entry with the
    /// same id, feature, scope and target, is in <paramref name="entries"/> is left out, so the write lists once with how it
    /// ended; an entry with that id for another feature, scope or target is no record of it. One with no record lists as started
    /// while it is younger than <see cref="ClaimLease"/> at <paramref name="now"/>, and as
    /// <see cref="StartedNotRecordedOutcome"/> from then on. The log itself is never rewritten.
    /// </summary>
    public static IReadOnlyList<WriteLogEntry> AsListed(IReadOnlyList<WriteLogEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var recorded = entries
            .Where(entry => entry.ActionId is not null && !IsStarted(entry))
            .Select(RecordKey)
            .ToHashSet();
        var listed = new List<WriteLogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (!IsStarted(entry))
            {
                listed.Add(entry);
            }
            else if (!recorded.Contains(RecordKey(entry)))
            {
                listed.Add(now - entry.At >= ClaimLease ? entry with { Outcome = StartedNotRecordedOutcome } : entry);
            }
        }

        return listed;
    }

    private static (string FeatureId, string Action, string Target, string? ActionId) RecordKey(WriteLogEntry entry) =>
        (entry.FeatureId, entry.Action, entry.Target, entry.ActionId);

    private static bool IsStarted(WriteLogEntry entry) =>
        entry.ActionId is not null
        && entry.ApprovedBy is null
        && string.Equals(entry.Outcome, StartedOutcome, StringComparison.Ordinal);

    private async Task RecordStartedAsync(
        string featureId, string scope, string target, string outcome, string actionId, string? signedAs, CancellationToken ct)
    {
        if (string.Equals(outcome, StartedOutcome, StringComparison.Ordinal) || IsApproval(outcome))
        {
            throw new WriteNotRecordableException($"A write's record cannot read '{outcome}'; it says how the write ended.");
        }

        await _writes.AppendAsync(
                new WriteLogEntry(_time.GetUtcNow(), featureId, scope, target, outcome, null, actionId, signedAs),
                ct)
            .ConfigureAwait(false);
    }
}
