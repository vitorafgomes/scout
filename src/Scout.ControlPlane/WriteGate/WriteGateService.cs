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
    /// the action must be one the owner approved for this feature and scope; an irreversible scope needs one,
    /// and recording it marks the action executed, so it is recorded once.
    /// </summary>
    /// <exception cref="WriteNotRecordableException">
    /// The scope is undeclared or a read, a reversible write the owner has not granted, irreversible without
    /// an action, or the action is not approved for it or was already recorded.
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

        if (row.Access == ScopeAccess.Write
            && !(await _states.GetAsync(featureId, ct).ConfigureAwait(false)).Granted)
        {
            throw new WriteNotRecordableException(NotGranted(featureId));
        }

        if (row.Access == ScopeAccess.IrreversibleWrite && actionId is null)
        {
            throw new WriteNotRecordableException(
                $"{scope} is irreversible; it is recorded only with an action the owner approved.");
        }

        string? approvedBy = null;
        if (actionId is not null)
        {
            var action = string.IsNullOrWhiteSpace(actionId)
                ? null
                : await _actions.FindAsync(actionId, ct).ConfigureAwait(false);
            if (action is not { Status: PendingActionStatus.Approved })
            {
                throw new WriteNotRecordableException($"Action '{actionId}' is not approved by the owner.");
            }

            if (!string.Equals(action.FeatureId, featureId, StringComparison.Ordinal)
                || !string.Equals(action.Action, scope, StringComparison.Ordinal))
            {
                throw new WriteNotRecordableException(
                    $"Action '{actionId}' was approved for {action.FeatureId} {action.Action}, not {featureId} {scope}.");
            }

            try
            {
                await _actions.MarkExecutedAsync(actionId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PendingActionNotApprovedException or PendingActionNotFoundException)
            {
                throw new WriteNotRecordableException($"Action '{actionId}' is not approved by the owner.");
            }

            approvedBy = "owner";
        }

        await _writes.AppendAsync(
                new WriteLogEntry(DateTimeOffset.UtcNow, featureId, scope, target, outcome, approvedBy, actionId),
                ct)
            .ConfigureAwait(false);
    }

    private FeatureScopeRow? FindRow(string featureId, string scope) =>
        string.IsNullOrWhiteSpace(featureId)
            ? null
            : _catalog.Find(featureId)?.ScopeMatrix?
                .FirstOrDefault(row => string.Equals(row.Scope, scope, StringComparison.Ordinal));

    private static string NotDeclared(string featureId, string scope) => $"{scope} is not declared by {featureId}";

    private static string IsARead(string scope) => $"{scope} is a read";

    private static string NotGranted(string featureId) =>
        $"The owner has not allowed {featureId} to write; allow it on the Features screen of Scout Desktop.";
}
