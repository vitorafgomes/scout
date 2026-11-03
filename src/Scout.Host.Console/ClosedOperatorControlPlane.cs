using Scout.Operator;

namespace Scout.Host.Console;

/// <summary>
/// The <see cref="IOperatorControlPlane"/> when the worker runs without Scout Desktop's control plane: every call
/// fails with a sentence saying the operator is off, so no video project starts or runs.
/// </summary>
internal sealed class ClosedOperatorControlPlane : IOperatorControlPlane
{
    internal const string Reason = "Scout Desktop's control plane is not available, so the video operator is off.";

    public Task<OperatorMandate> GetMandateAsync(CancellationToken ct)
        => Task.FromException<OperatorMandate>(new InvalidOperationException(Reason));

    public Task<OperatorProjectState> SaveProjectAsync(OperatorProjectState project, string? originReference, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new InvalidOperationException(Reason));

    public Task<QueuedProject?> NextQueuedAsync(CancellationToken ct)
        => Task.FromException<QueuedProject?>(new InvalidOperationException(Reason));

    public Task<OperatorProjectState> GetProjectAsync(string id, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new InvalidOperationException(Reason));

    public Task<IReadOnlyList<OperatorProjectState>> ListRecentAsync(int limit, CancellationToken ct)
        => Task.FromException<IReadOnlyList<OperatorProjectState>>(new InvalidOperationException(Reason));

    public Task<string> GetMediaDirectoryAsync(string projectId, CancellationToken ct)
        => Task.FromException<string>(new InvalidOperationException(Reason));

    public Task ReportReadinessAsync(OperatorReadiness readiness, CancellationToken ct)
        => Task.FromException(new InvalidOperationException(Reason));
}
