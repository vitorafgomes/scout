namespace Scout.Operator.Tests.Fakes;

/// <summary>
/// An <see cref="IOperatorControlPlane"/> that holds projects by id, keeps a copy of every project saved, as it
/// was when saved, and answers a save with the project, or with a failure for the statuses
/// <see cref="FailOn"/> names. A save with an empty id creates the project <c>new-1</c>. With
/// <see cref="RefuseStaleScenes"/> it refuses a save whose scene ids, order and locks are not the stored ones, stricter
/// than the control plane, which keeps the stored order and locks; <see cref="OwnerActs"/> changes the stored project after the given save, as the owner would;
/// <see cref="NoLongerProducingOn"/> refuses the given save the way the control plane refuses a save on a project the
/// owner sent back to production meanwhile, and <see cref="NoLongerAssemblingOn"/> the same for assembly. With
/// <see cref="Revoked"/> it behaves as the control plane after the owner revoked YouTube writes: only the project of the
/// claimed action may be read, and saved with nothing but its publication changed, and a record is logged "after revoke",
/// consumes the action and is refused.
/// </summary>
internal sealed class RecordingControlPlane : IOperatorControlPlane
{
    private readonly Lock _lock = new();
    private readonly List<(OperatorProjectState Project, string? Origin)> _saves = [];
    private readonly Dictionary<string, OperatorProjectState> _stored = new(StringComparer.Ordinal);
    private readonly Queue<QueuedProject> _queued = new();

    public OperatorMandate Mandate { get; init; } = Given.Mandate();

    public Func<string, bool> FailOn { get; init; } = _ => false;

    public Func<OperatorProjectState, bool> FailOnProject { get; init; } = _ => false;

    public Exception? NextQueuedFailure { get; init; }

    public int NextQueuedCalls { get; private set; }

    public string MediaDirectory { get; init; } = "/nonexistent/scout-media";

    public Exception? MediaDirectoryFailure { get; init; }

    public Exception? ReportFailure { get; init; }

    public List<OperatorReadiness> Reports { get; } = [];

    public bool RefuseStaleScenes { get; init; }

    public Func<int, bool> NoLongerProducingOn { get; init; } = _ => false;

    public Func<int, bool> NoLongerAssemblingOn { get; init; } = _ => false;

    public Func<int, OperatorProjectState, OperatorProjectState?> OwnerActs { get; init; } = (_, _) => null;

    public IReadOnlyList<(OperatorProjectState Project, string? Origin)> Saves
    {
        get
        {
            lock (_lock)
            {
                return [.. _saves];
            }
        }
    }

    public void Store(OperatorProjectState project)
    {
        lock (_lock)
        {
            _stored[project.Id] = project;
        }
    }

    public void Queue(QueuedProject project)
    {
        lock (_lock)
        {
            _stored[project.Project.Id] = project.Project;
            _queued.Enqueue(project);
        }
    }

    public Task<OperatorMandate> GetMandateAsync(CancellationToken ct) => Task.FromResult(Mandate);

    public Task<OperatorProjectState> SaveProjectAsync(OperatorProjectState project, string? originReference, CancellationToken ct)
    {
        lock (_lock)
        {
            _saves.Add((project, originReference));
            if (FailOn(project.Status) || FailOnProject(project))
            {
                return Task.FromException<OperatorProjectState>(new ControlPlaneUnavailableException("Scout could not reach its control plane."));
            }

            if (RefuseOnProject(project) || (Revoked && !(project.Id == _executing && OnlyPublicationChanged(project))))
            {
                return Task.FromException<OperatorProjectState>(new InvalidOperationException(NotGranted));
            }

            if (NoLongerProducingOn(_saves.Count))
            {
                return Task.FromException<OperatorProjectState>(new InvalidOperationException("The project is no longer producing."));
            }

            if (NoLongerAssemblingOn(_saves.Count))
            {
                return Task.FromException<OperatorProjectState>(new InvalidOperationException("The project is no longer assembling."));
            }

            if (RefuseStaleScenes
                && _stored.TryGetValue(project.Id, out var stored)
                && stored.Scenes.Count > 0
                && !stored.Scenes.Select(SceneList).Order().SequenceEqual(project.Scenes.Select(SceneList).Order()))
            {
                return Task.FromException<OperatorProjectState>(
                    new InvalidOperationException("The scenes changed since the worker read them; read the project again."));
            }

            var saved = string.IsNullOrEmpty(project.Id) ? project with { Id = "new-1" } : project;
            _stored[saved.Id] = OwnerActs(_saves.Count, saved) ?? saved;
            return Task.FromResult(saved);
        }
    }

    public Task<QueuedProject?> NextQueuedAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            NextQueuedCalls++;
            if (NextQueuedFailure is not null)
            {
                return Task.FromException<QueuedProject?>(NextQueuedFailure);
            }

            return Task.FromResult(_queued.TryDequeue(out var next) ? next : null);
        }
    }

    public Task<OperatorProjectState> GetProjectAsync(string id, CancellationToken ct)
    {
        lock (_lock)
        {
            if (Revoked && id != _executing)
            {
                return Task.FromException<OperatorProjectState>(new InvalidOperationException(NotGranted));
            }

            return _stored.TryGetValue(id, out var project)
                ? Task.FromResult(project)
                : Task.FromException<OperatorProjectState>(new InvalidOperationException($"Unknown project '{id}'."));
        }
    }

    public Task<IReadOnlyList<OperatorProjectState>> ListRecentAsync(int limit, CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OperatorProjectState>>([.. _stored.Values.Reverse().Take(limit)]);
        }
    }

    public Task<string> GetMediaDirectoryAsync(string projectId, CancellationToken ct)
        => MediaDirectoryFailure is null ? Task.FromResult(MediaDirectory) : Task.FromException<string>(MediaDirectoryFailure);

    public Task ReportReadinessAsync(OperatorReadiness readiness, CancellationToken ct)
    {
        lock (_lock)
        {
            Reports.Add(readiness);
            return ReportFailure is null ? Task.CompletedTask : Task.FromException(ReportFailure);
        }
    }

    public Queue<OperatorAction> Approved { get; } = new();

    public Exception? ActionFailure { get; set; }

    public Task<OperatorAction?> NextApprovedActionAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (ActionFailure is not null)
            {
                return Task.FromException<OperatorAction?>(ActionFailure);
            }

            var next = Approved.TryPeek(out var head) ? head : null;
            _executing = next?.Target.Split(':')[0];
            return Task.FromResult(next);
        }
    }

    public List<(OperatorAction Action, string Outcome)> Recorded { get; } = [];

    public Exception? RecordFailure { get; set; }

    public Task RecordActionAsync(OperatorAction action, string outcome, CancellationToken ct)
    {
        lock (_lock)
        {
            if (RecordFailure is not null)
            {
                return Task.FromException(RecordFailure);
            }

            if (Revoked)
            {
                Recorded.Add((action, $"{outcome} after revoke"));
                Approved.TryDequeue(out _);
                _executing = null;
                return Task.FromException(new WriteRevokedException(RevokedRecord));
            }

            Recorded.Add((action, outcome));
            if (Approved.TryPeek(out var next) && next.Id == action.Id)
            {
                Approved.Dequeue();
            }

            return Task.CompletedTask;
        }
    }

    public const string NotGranted = "The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.";

    public const string RevokedRecord = "The owner revoked youtube's write permission before this action ran.";

    private string? _executing;

    public bool Revoked { get; set; }

    public Func<OperatorProjectState, bool> RefuseOnProject { get; init; } = _ => false;

    private bool OnlyPublicationChanged(OperatorProjectState project)
        => _stored.TryGetValue(project.Id, out var stored) && (project with { Publication = stored.Publication }) == stored;

    private static string SceneList(OperatorScene scene) => $"{scene.Id}|{scene.Order}|{scene.Locked}";
}
