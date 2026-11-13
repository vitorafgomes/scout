using Scout.Llm.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// The <see cref="IWriteGate"/> and <see cref="IApprovedActions"/> when the worker runs without Scout Desktop's control
/// plane: every write is refused and no action is ever approved.
/// </summary>
internal sealed class ClosedWriteGate : IWriteGate, IApprovedActions
{
    internal const string Reason = "Writes are off: Scout Desktop's control plane is not available.";

    public Task<WriteGateDecision> RequestAsync(
        string featureId,
        string scope,
        string target,
        string summary,
        CancellationToken cancellationToken)
        => Task.FromResult(new WriteGateDecision(WriteGateDecisionKind.Refused, Reason, null));

    public Task RecordAsync(
        string featureId,
        string scope,
        string target,
        string outcome,
        string? actionId,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<ApprovedAction?> ClaimNextAsync(string featureId, CancellationToken cancellationToken)
        => Task.FromResult<ApprovedAction?>(null);

    public Task<bool> CheckGrantAsync(ApprovedAction action, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
