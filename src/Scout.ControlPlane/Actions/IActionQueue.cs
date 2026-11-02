namespace Scout.ControlPlane.Actions;

/// <summary>
/// Queue of irreversible actions waiting for the owner. A decision covers one
/// action and only comes through the control plane.
/// </summary>
public interface IActionQueue
{
    /// <summary>Queue an action as pending and return it with its id and request time.</summary>
    Task<PendingAction> EnqueueAsync(
        string featureId,
        string action,
        string target,
        string summary,
        CancellationToken ct);

    /// <summary>Actions still waiting for a decision, oldest first.</summary>
    Task<IReadOnlyList<PendingAction>> ListPendingAsync(CancellationToken ct);

    /// <summary>Approve a pending action, with the owner's optional reason.</summary>
    /// <exception cref="PendingActionNotFoundException">The id is unknown.</exception>
    /// <exception cref="PendingActionAlreadyDecidedException">The action was already decided.</exception>
    Task<PendingAction> ApproveAsync(string id, string? reason, CancellationToken ct);

    /// <summary>Reject a pending action, with the owner's optional reason.</summary>
    /// <exception cref="PendingActionNotFoundException">The id is unknown.</exception>
    /// <exception cref="PendingActionAlreadyDecidedException">The action was already decided.</exception>
    Task<PendingAction> RejectAsync(string id, string? reason, CancellationToken ct);

    /// <summary>Mark an approved action as executed, so it is recorded once.</summary>
    /// <exception cref="PendingActionNotFoundException">The id is unknown.</exception>
    /// <exception cref="PendingActionNotApprovedException">The action is not approved, or was already executed.</exception>
    Task<PendingAction> MarkExecutedAsync(string id, CancellationToken ct);

    /// <summary>The action with <paramref name="id"/> in any status, or null when the queue does not hold it.</summary>
    Task<PendingAction?> FindAsync(string id, CancellationToken ct);
}
