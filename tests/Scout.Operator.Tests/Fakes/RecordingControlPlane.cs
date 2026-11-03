namespace Scout.Operator.Tests.Fakes;

/// <summary>
/// An <see cref="IOperatorControlPlane"/> that holds projects by id, keeps a copy of every project saved, as it
/// was when saved, and answers a save with the project, or with a failure for the statuses
/// <see cref="FailOn"/> names. A save with an empty id creates the project <c>new-1</c>.
/// </summary>
internal sealed class RecordingControlPlane : IOperatorControlPlane
{
    private readonly Lock _lock = new();
    private readonly List<(OperatorProjectState Project, string? Origin)> _saves = [];
    private readonly Dictionary<string, OperatorProjectState> _stored = new(StringComparer.Ordinal);
    private readonly Queue<QueuedProject> _queued = new();

    public OperatorMandate Mandate { get; init; } = Given.Mandate();

    public Func<string, bool> FailOn { get; init; } = _ => false;

    public Exception? NextQueuedFailure { get; init; }

    public int NextQueuedCalls { get; private set; }

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
            if (FailOn(project.Status))
            {
                return Task.FromException<OperatorProjectState>(new InvalidOperationException("Scout could not reach its control plane."));
            }

            var saved = string.IsNullOrEmpty(project.Id) ? project with { Id = "new-1" } : project;
            _stored[saved.Id] = saved;
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
}
