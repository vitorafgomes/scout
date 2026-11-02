namespace Scout.ControlPlane.Actions;

/// <summary>Where a queued action stands.</summary>
public enum PendingActionStatus
{
    /// <summary>Waiting for the owner.</summary>
    Pending,

    /// <summary>The owner approved it.</summary>
    Approved,

    /// <summary>The owner rejected it.</summary>
    Rejected,

    /// <summary>The owner approved it and the worker recorded the write; it cannot be recorded again.</summary>
    Executed,
}

/// <summary>
/// An irreversible action a feature asked to perform; it runs only after the
/// owner approves it in the desktop. <c>Reason</c> carries the owner's note on the
/// decision, approval or rejection.
/// </summary>
public sealed record PendingAction(
    string Id,
    string FeatureId,
    string Action,
    string Target,
    string Summary,
    DateTimeOffset RequestedAt,
    PendingActionStatus Status,
    DateTimeOffset? DecidedAt,
    string? Reason = null);
