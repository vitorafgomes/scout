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

/// <summary>The gate's answer; <c>ActionId</c> is set only when the write was queued.</summary>
public sealed record WriteDecision(WriteDecisionKind Kind, string Reason, string? ActionId = null);

/// <summary>Thrown when a write cannot be recorded because it was not permitted.</summary>
public sealed class WriteNotRecordableException : Exception
{
    /// <summary>Build the exception with the reason the record was refused.</summary>
    public WriteNotRecordableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The only path through which the worker asks to write and records what it wrote.
/// A write runs only when its feature declares the scope as a write and the owner
/// granted the feature; an irreversible one is queued for the owner instead. The
/// grant is read from the state store on every request. The gate never grants and
/// never decides an action.
/// </summary>
public sealed class WriteGateService
{
    /// <summary>
    /// Outcome of the write-log entry that records the owner's approval of an action made in the desktop; it is not
    /// the record of the write, so it never counts as one.
    /// </summary>
    public const string ApprovedOutcome = "approved";

    private const string Waiting = "Waiting for the owner's approval on the Review screen of Scout Desktop.";

    private readonly IFeatureCatalog _catalog;
    private readonly IFeatureStateStore _states;
    private readonly IActionQueue _actions;
    private readonly IWriteLog _writes;

    /// <summary>Build the gate over the catalog, the feature state, the queue and the log.</summary>
    public WriteGateService(
        IFeatureCatalog catalog,
        IFeatureStateStore states,
        IActionQueue actions,
        IWriteLog writes)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    }

    /// <summary>The approval queue the gate records against.</summary>
    internal IActionQueue Actions => _actions;

    /// <summary>When the owner last granted <paramref name="featureId"/>'s writes; null while not granted.</summary>
    internal async Task<DateTimeOffset?> GrantedAtAsync(string featureId, CancellationToken ct) =>
        (await _states.GetAsync(featureId, ct).ConfigureAwait(false)).GrantedAt;

    /// <summary>How long a worker's claim on an approved action holds before another run may take it over.</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(30);

    /// <summary>
    /// For a worker that runs a feature's approved actions itself: claim the oldest approved action of
    /// <paramref name="featureId"/> that was requested under the current grant, or an executing one whose claim lapsed.
    /// Null when the feature holds no grant, or nothing waits. An action requested before the owner granted, or
    /// re-granted, the feature is never handed out: the grant it ran under is gone.
    /// </summary>
    public async Task<PendingAction?> ClaimNextApprovedAsync(string featureId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        var grantedAt = await GrantedAtAsync(featureId, ct).ConfigureAwait(false);
        if (grantedAt is null)
        {
            return null;
        }

        return await _actions.ClaimNextAsync(featureId, ClaimLease, (action, _) => action.RequestedAt >= grantedAt, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Decide whether <paramref name="scope"/> of <paramref name="featureId"/> may be written now.</summary>
    public async Task<WriteDecision> RequestAsync(
        string featureId,
        string scope,
        string target,
        string summary,
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

        var queued = await _actions.EnqueueAsync(featureId, scope, target, summary, ct).ConfigureAwait(false);
        return new WriteDecision(WriteDecisionKind.Queued, Waiting, queued.Id);
    }

    /// <summary>
    /// Append a write the worker performed to the write log. With <paramref name="actionId"/>
    /// the action must be one the owner approved, or a worker claimed, for this feature and scope; an irreversible
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
    public async Task RecordAsync(
        string featureId,
        string scope,
        string target,
        string outcome,
        string? actionId,
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
                    new WriteLogEntry(DateTimeOffset.UtcNow, featureId, scope, target, outcome, null, null),
                    ct)
                .ConfigureAwait(false);
            return;
        }

        var action = string.IsNullOrWhiteSpace(actionId)
            ? null
            : await _actions.FindAsync(actionId, ct).ConfigureAwait(false);
        var matches = action is not null
            && string.Equals(action.FeatureId, featureId, StringComparison.Ordinal)
            && string.Equals(action.Action, scope, StringComparison.Ordinal);
        if (!granted)
        {
            if (matches && action!.Status == PendingActionStatus.Executing)
            {
                await RecordOnceAsync(action, target, $"{outcome} after revoke", ct).ConfigureAwait(false);
            }

            throw new WriteNotRecordableException(Revoked(featureId));
        }

        if (action is not { Status: PendingActionStatus.Approved or PendingActionStatus.Executing })
        {
            throw new WriteNotRecordableException(NotApproved(actionId));
        }

        if (!matches)
        {
            throw new WriteNotRecordableException(
                $"Action '{actionId}' was approved for {action.FeatureId} {action.Action}, not {featureId} {scope}.");
        }

        if (await RecordOnceAsync(action, target, outcome, ct).ConfigureAwait(false))
        {
            throw new WriteNotRecordableException($"Action '{actionId}' is already recorded in the write log.");
        }
    }

    private async Task<bool> RecordOnceAsync(PendingAction action, string target, string outcome, CancellationToken ct)
    {
        var alreadyLogged = false;
        try
        {
            await _actions.ExecuteAsync(
                    action.Id,
                    async token =>
                    {
                        alreadyLogged = (await _writes.ListSinceAsync(action.RequestedAt, token).ConfigureAwait(false))
                            .Any(entry => string.Equals(entry.ActionId, action.Id, StringComparison.Ordinal)
                                && !string.Equals(entry.Outcome, ApprovedOutcome, StringComparison.Ordinal));
                        if (!alreadyLogged)
                        {
                            await _writes.AppendAsync(
                                    new WriteLogEntry(DateTimeOffset.UtcNow, action.FeatureId, action.Action, target, outcome, "owner", action.Id),
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
}
