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

    /// <summary>
    /// Queue <paramref name="actions"/> of <paramref name="featureId"/> as approved by the owner, in one write and in
    /// their order, and return them with their ids.
    /// </summary>
    Task<IReadOnlyList<PendingAction>> EnqueueApprovedAsync(
        string featureId,
        IReadOnlyList<(string Action, string Target, string Summary)> actions,
        CancellationToken ct);

    /// <summary>
    /// Under the queue's gate, claim the oldest action of <paramref name="featureId"/> that is approved, or executing
    /// with a claim older than <paramref name="lease"/>, and that <paramref name="eligible"/> accepts given every
    /// approved or executing action of the feature, oldest first; it becomes executing with the claim time. Null when
    /// none is eligible, and then the file is not written.
    /// </summary>
    Task<PendingAction?> ClaimNextAsync(
        string featureId,
        TimeSpan lease,
        Func<PendingAction, IReadOnlyList<PendingAction>, bool> eligible,
        CancellationToken ct);

    /// <summary>
    /// Under the queue's gate, reject with <paramref name="reason"/> the approved actions of <paramref name="featureId"/>
    /// that <paramref name="match"/> accepts given every approved action of the feature, oldest first; executing ones
    /// are never touched. The rejected actions, none when nothing matched, and then the file is not written.
    /// </summary>
    Task<IReadOnlyList<PendingAction>> RejectApprovedAsync(
        string featureId,
        Func<PendingAction, IReadOnlyList<PendingAction>, bool> match,
        string reason,
        CancellationToken ct);

    /// <summary>
    /// Actions still waiting for a decision, and approved ones a worker is executing, which cannot be decided, oldest first.
    /// </summary>
    Task<IReadOnlyList<PendingAction>> ListPendingAsync(CancellationToken ct);

    /// <summary>The actions of <paramref name="featureId"/> the owner approved that are not recorded yet, approved or executing, oldest first.</summary>
    Task<IReadOnlyList<PendingAction>> ListOpenAsync(string featureId, CancellationToken ct);

    /// <summary>Approve a pending action, with the owner's optional reason.</summary>
    /// <exception cref="PendingActionNotFoundException">The id is unknown.</exception>
    /// <exception cref="PendingActionAlreadyDecidedException">The action was already decided.</exception>
    Task<PendingAction> ApproveAsync(string id, string? reason, CancellationToken ct);

    /// <summary>Reject a pending action, with the owner's optional reason.</summary>
    /// <exception cref="PendingActionNotFoundException">The id is unknown.</exception>
    /// <exception cref="PendingActionAlreadyDecidedException">The action was already decided.</exception>
    Task<PendingAction> RejectAsync(string id, string? reason, CancellationToken ct);

    /// <summary>Mark an approved or claimed action as executed, so it is recorded once.</summary>
    /// <exception cref="PendingActionNotFoundException">The id is unknown.</exception>
    /// <exception cref="PendingActionNotApprovedException">The action is not approved, or was already executed.</exception>
    Task<PendingAction> MarkExecutedAsync(string id, CancellationToken ct);

    /// <summary>
    /// Under the queue's gate: require the action approved or claimed, run <paramref name="record"/>, then mark it
    /// executed. A <paramref name="record"/> that throws leaves the action as it was.
    /// </summary>
    /// <exception cref="PendingActionNotFoundException">The id is unknown.</exception>
    /// <exception cref="PendingActionNotApprovedException">The action is not approved, or was already executed.</exception>
    Task<PendingAction> ExecuteAsync(string id, Func<CancellationToken, Task> record, CancellationToken ct);

    /// <summary>The action with <paramref name="id"/> in any status, or null when the queue does not hold it.</summary>
    Task<PendingAction?> FindAsync(string id, CancellationToken ct);
}
