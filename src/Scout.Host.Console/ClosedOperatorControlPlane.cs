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
        => Task.FromException<OperatorMandate>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorProjectState> SaveProjectAsync(OperatorProjectState project, string? originReference, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException(Reason));

    public Task<QueuedProject?> NextQueuedAsync(CancellationToken ct)
        => Task.FromException<QueuedProject?>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorProjectState> GetProjectAsync(string id, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException(Reason));

    public Task<IReadOnlyList<OperatorProjectState>> ListRecentAsync(int limit, CancellationToken ct)
        => Task.FromException<IReadOnlyList<OperatorProjectState>>(new ControlPlaneUnavailableException(Reason));

    public Task<string> GetMediaDirectoryAsync(string projectId, CancellationToken ct)
        => Task.FromException<string>(new ControlPlaneUnavailableException(Reason));

    public Task ReportReadinessAsync(OperatorReadiness readiness, CancellationToken ct)
        => Task.FromException(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorAction?> NextApprovedActionAsync(CancellationToken ct)
        => Task.FromException<OperatorAction?>(new ControlPlaneUnavailableException(Reason));

    public Task RecordActionAsync(OperatorAction action, string outcome, CancellationToken ct)
        => Task.FromException(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorComments> GetCommentsAsync(CancellationToken ct)
        => Task.FromException<OperatorComments>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorComments> SaveCommentsAsync(OperatorComments comments, CancellationToken ct)
        => Task.FromException<OperatorComments>(new ControlPlaneUnavailableException(Reason));

    public Task<string> GetVoiceSamplesDirectoryAsync(CancellationToken ct)
        => Task.FromException<string>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorProjectState> EditSceneTextAsync(string projectId, string sceneId, string text, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorProjectState> RegenerateNarrationAsync(string projectId, string sceneId, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorProjectState> RegenerateSceneAsync(string projectId, string sceneId, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorProjectState> SetSceneLockedAsync(string projectId, string sceneId, bool locked, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorProjectState> ReorderScenesAsync(string projectId, IReadOnlyList<string> sceneIds, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorProjectState> ProduceProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorProjectState> DelegateProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException(Reason));

    public Task<OperatorProjectState> RequestPresenterAsync(string projectId, string feedback, string prompt, CancellationToken ct)
        => Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException(Reason));
}
