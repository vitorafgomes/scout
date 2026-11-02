using Scout.Llm.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// The <see cref="IWriteGate"/> when the worker runs without Scout Desktop's control
/// plane: every write is refused.
/// </summary>
internal sealed class ClosedWriteGate : IWriteGate
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
}
