namespace Scout.ControlPlane.Controls;

/// <summary>
/// The owner's controls on running jobs, waiting for the worker: queued by the desktop, taken by the worker once and never
/// handed out again, then settled once with how they ended.
/// </summary>
public interface IOwnerControlQueue
{
    /// <summary>Keep <paramref name="control"/>, not taken yet.</summary>
    Task EnqueueAsync(OwnerControl control, CancellationToken ct);

    /// <summary>
    /// Under the queue's gate and in one read, the oldest control not taken yet, marked taken at <paramref name="now"/>
    /// before it is returned, so no later call returns it again (null when none waits), and the controls taken before
    /// <paramref name="lapsedBefore"/> and never settled; when nothing is taken the file is not written.
    /// </summary>
    Task<(OwnerControl? Taken, IReadOnlyList<OwnerControl> Lapsed)> TakeNextAsync(DateTimeOffset now, DateTimeOffset lapsedBefore, CancellationToken ct);

    /// <summary>
    /// Under the queue's gate, mark the taken control <paramref name="id"/> settled at <paramref name="now"/> and return it;
    /// null when no taken control has that id or it was settled already, and then the file is not written.
    /// </summary>
    Task<OwnerControl?> SettleAsync(string id, DateTimeOffset now, CancellationToken ct);

    /// <summary>The controls taken before <paramref name="before"/> and not settled yet, oldest first.</summary>
    Task<IReadOnlyList<OwnerControl>> UnsettledAsync(DateTimeOffset before, CancellationToken ct);
}
