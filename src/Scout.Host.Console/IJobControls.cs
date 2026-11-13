namespace Scout.Host.Console;

/// <summary>One click of the owner in Scout Desktop on a running job, as the control plane hands it to the worker.</summary>
/// <param name="Id">The control's id, which its record names.</param>
/// <param name="Kind"><c>audit</c> or <c>tests</c>.</param>
/// <param name="Owner">Repository owner.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="Number">The pull request number of a test run; null for an audit.</param>
/// <param name="Action"><c>pause</c>, <c>continue</c> or <c>stop</c>.</param>
internal sealed record JobControl(string Id, string Kind, string Owner, string Repo, int? Number, string Action);

/// <summary>
/// The owner's controls on running jobs, from the control plane: each is handed to the worker once and settled once with
/// the sentence that says how it ended.
/// </summary>
internal interface IJobControls
{
    /// <summary>The next control waiting, now taken by this worker; null when none waits.</summary>
    Task<JobControl?> TakeNextAsync(CancellationToken ct);

    /// <summary>Record how control <paramref name="id"/> ended: its <paramref name="status"/>, <c>applied</c>, <c>not applied</c> or <c>not confirmed</c>, and the sentence.</summary>
    Task SettleAsync(string id, string status, string outcome, CancellationToken ct);
}
