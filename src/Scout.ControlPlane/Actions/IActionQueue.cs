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

    /// <summary>Queue an action as pending with the <paramref name="payload"/> its run needs, and return it with its id and request time.</summary>
    Task<PendingAction> EnqueueAsync(
        string featureId,
        string action,
        string target,
        string summary,
        string? payload,
        CancellationToken ct);

    /// <summary>
    /// Under the queue's gate: the action of <paramref name="featureId"/> that <paramref name="sameAs"/> accepts and that is
    /// neither rejected nor executed with a failed outcome, when one exists; otherwise a new action, queued as approved by the
    /// owner with <paramref name="payload"/>, and whether it was added. So one owner's click is one action, and a failed or rejected one may be asked again.
    /// </summary>
    Task<(PendingAction Action, bool Added)> EnqueueApprovedOnceAsync(
        string featureId,
        string action,
        string target,
        string summary,
        string payload,
        Func<PendingAction, bool> sameAs,
        CancellationToken ct);

    /// <summary>
    /// Under the queue's gate and from one read, for each of <paramref name="actions"/> in order: the first action of
    /// <paramref name="featureId"/> that <paramref name="answers"/> accepts for the item's card key, the ones this call queued
    /// included, when one exists; otherwise the item queued as approved by the owner with its card key, in one new group on
    /// <paramref name="repository"/> that is closed at once. Each item's action and whether it was added, in their order;
    /// the items added are written in one write, and nothing is written when none was added.
    /// </summary>
    Task<IReadOnlyList<(PendingAction Action, bool Added)>> EnqueueApprovedOnceEachAsync(
        string featureId,
        string repository,
        IReadOnlyList<(string CardKey, string Action, string Target, string Summary, string Payload)> actions,
        Func<PendingAction, string, bool> answers,
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
    /// Under the queue's gate, reject with <paramref name="reason"/> every action of <paramref name="featureId"/> that has not
    /// started: still waiting for the owner's decision, or approved and not yet claimed by a worker; an executing one is never
    /// touched. The rejected actions, none when nothing matched, and then the file is not written.
    /// </summary>
    Task<IReadOnlyList<PendingAction>> RejectUnstartedAsync(string featureId, string reason, CancellationToken ct);

    /// <summary>
    /// Under the queue's gate, return the claimed action <paramref name="id"/> to approved, without its claim time, so a
    /// later claim may take it. An action that is no longer executing is left as it is, and then the file is not written.
    /// </summary>
    Task ReleaseAsync(string id, CancellationToken ct);

    /// <summary>
    /// Under the queue's gate, reject with <paramref name="reason"/> the actions of <paramref name="featureId"/> a worker
    /// claimed and is executing that <paramref name="match"/> accepts. The rejected actions, none when nothing matched, and
    /// then the file is not written.
    /// </summary>
    Task<IReadOnlyList<PendingAction>> RejectExecutingAsync(
        string featureId,
        Func<PendingAction, bool> match,
        string reason,
        CancellationToken ct);

    /// <summary>
    /// Actions still waiting for a decision, approved ones no worker has claimed yet, and approved ones a worker is
    /// executing, oldest first; only the waiting ones can be decided.
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

    /// <summary>
    /// As <see cref="ExecuteAsync(string, Func{CancellationToken, Task}, CancellationToken)"/>, keeping on the executed action
    /// the <paramref name="outcome"/> the write log records and the <paramref name="result"/> the owner reads.
    /// </summary>
    Task<PendingAction> ExecuteAsync(
        string id, string? outcome, string? result, Func<CancellationToken, Task> record, CancellationToken ct);

    /// <summary>Every action of <paramref name="featureId"/> the queue still holds, in any status, oldest first.</summary>
    Task<IReadOnlyList<PendingAction>> ListAllAsync(string featureId, CancellationToken ct);

    /// <summary>The action with <paramref name="id"/> in any status, or null when the queue does not hold it.</summary>
    Task<PendingAction?> FindAsync(string id, CancellationToken ct);

    /// <summary>
    /// Queue an action as pending with the <paramref name="payload"/> its run needs, in the request
    /// <paramref name="groupId"/> on <paramref name="repository"/>, each null when the request named none, and return it.
    /// An action that joins a group already closed is queued all the same.
    /// </summary>
    Task<PendingAction> EnqueueAsync(
        string featureId,
        string action,
        string target,
        string summary,
        string? payload,
        string? groupId,
        string? repository,
        CancellationToken ct);

    /// <summary>
    /// Under the queue's gate, stamp every action of <paramref name="groupId"/>, in any status, with the time the request
    /// ended, keeping the stamp of one already closed, and return how many actions the group holds; nothing else changes.
    /// </summary>
    Task<int> CloseGroupAsync(string groupId, CancellationToken ct);

    /// <summary>Every action the queue holds that <paramref name="match"/> accepts, in any status, oldest first, from one read.</summary>
    Task<IReadOnlyList<PendingAction>> ListWhereAsync(Func<PendingAction, bool> match, CancellationToken ct);

    /// <summary>
    /// Under the queue's gate and from one read, hand <paramref name="decide"/> every action of <paramref name="groupId"/>, in
    /// any status, oldest first; it answers the waiting actions to decide, by id, each to approve or reject with its reason,
    /// and they are decided together in one write, none and no write when it answers none. An exception from
    /// <paramref name="decide"/> leaves the file as it was. The group's actions as they stand after.
    /// </summary>
    /// <exception cref="PendingActionNotFoundException">An answered id is not an action of the group.</exception>
    /// <exception cref="PendingActionAlreadyDecidedException">An answered action is not waiting.</exception>
    Task<IReadOnlyList<PendingAction>> DecideSetAsync(
        string groupId,
        Func<IReadOnlyList<PendingAction>, Task<IReadOnlyDictionary<string, (bool Approve, string? Reason)>>> decide,
        CancellationToken ct);
}
