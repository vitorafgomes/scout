namespace Scout.Operator.Tests.Fakes;

/// <summary>
/// An <see cref="IOperatorControlPlane"/> that holds projects by id, keeps a copy of every project saved, as it
/// was when saved, and answers a save with the project, or with a failure for the statuses
/// <see cref="FailOn"/> names. A save with an empty id creates the project <c>new-1</c>. With
/// <see cref="RefuseStaleScenes"/> it refuses a save whose scene ids, order and locks are not the stored ones, stricter
/// than the control plane, which keeps the stored order and locks; <see cref="OwnerActs"/> changes the stored project after the given save, as the owner would;
/// <see cref="NoLongerProducingOn"/> refuses the given save the way the control plane refuses a save on a project the
/// owner sent back to production meanwhile, <see cref="NoLongerAssemblingOn"/> the same for assembly and
/// <see cref="NoLongerTranscribingOn"/> for a transcription. With
/// <see cref="Revoked"/> it behaves as the control plane after the owner revoked YouTube writes: only the project of the
/// claimed action may be read, and saved with nothing but its publication changed, and a record is logged "after revoke",
/// consumes the action and is refused. The comments live in <see cref="Comments"/>; a save is kept in
/// <see cref="CommentSaves"/> as sent, refused like the control plane when it drops a stored comment, and stored with the
/// control plane's fetch time on a new comment and the sync request cleared by a sync at or after it or by a failed sync, one
/// with a non-empty error; a save with no error keeps the stored one unless it carries a newer sync time;
/// after a revoke the comments cannot be read and a save may change only the comment of the claimed action.
/// The voice samples directory is <see cref="VoiceSamplesDirectory"/>, or <see cref="VoiceSamplesFailure"/> when set.
/// A chat call is kept in <see cref="ChatCalls"/>, refused with <see cref="NotGranted"/> under <see cref="ChatNotGranted"/>,
/// and otherwise answers the stored project changed as the control plane would.
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

    public Func<int, bool> NoLongerTranscribingOn { get; init; } = _ => false;

    public Func<int, OperatorProjectState, OperatorProjectState?> OwnerActs { get; init; } = (_, _) => null;

    /// <summary>What the control plane makes of a save before it stores and answers it, as its pruning of presenter versions would.</summary>
    public Func<OperatorProjectState, OperatorProjectState> Keeps { get; init; } = p => p;

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

            if (NoLongerTranscribingOn(_saves.Count))
            {
                return Task.FromException<OperatorProjectState>(
                    new InvalidOperationException("The project is no longer transcribing; the worker's transcript was not saved."));
            }

            if (RefuseStaleScenes
                && _stored.TryGetValue(project.Id, out var stored)
                && stored.Scenes.Count > 0
                && !stored.Scenes.Select(SceneList).Order().SequenceEqual(project.Scenes.Select(SceneList).Order()))
            {
                return Task.FromException<OperatorProjectState>(
                    new InvalidOperationException("The scenes changed since the worker read them; read the project again."));
            }

            var saved = Keeps(string.IsNullOrEmpty(project.Id) ? project with { Id = "new-1" } : project);
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
            _executingTarget = next?.Target;
            return Task.FromResult(next);
        }
    }

    public bool GrantHeld { get; set; } = true;

    public Exception? GrantFailure { get; set; }

    public List<string> GrantChecks { get; } = [];

    public Task<bool> CheckGrantAsync(OperatorAction action, CancellationToken ct)
    {
        lock (_lock)
        {
            GrantChecks.Add(action.Id);
            return GrantFailure is not null ? Task.FromException<bool>(GrantFailure) : Task.FromResult(GrantHeld);
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
    private string? _executingTarget;

    public const string FetchedAt = "2026-11-03T12:00:00.0000000+00:00";

    public OperatorComments Comments { get; set; } = new([], [], null, null, null);

    public List<OperatorComments> CommentSaves { get; } = [];

    public Exception? CommentsFailure { get; set; }

    public Func<int, Exception?> CommentSaveFailure { get; init; } = _ => null;

    public int CommentReads { get; private set; }

    public Task<OperatorComments> GetCommentsAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            CommentReads++;
            if (Revoked)
            {
                return Task.FromException<OperatorComments>(new InvalidOperationException(NotGranted));
            }

            return CommentsFailure is null ? Task.FromResult(Comments) : Task.FromException<OperatorComments>(CommentsFailure);
        }
    }

    public Task<OperatorComments> SaveCommentsAsync(OperatorComments comments, CancellationToken ct)
    {
        lock (_lock)
        {
            CommentSaves.Add(comments);
            if (CommentSaveFailure(CommentSaves.Count) is { } failure)
            {
                return Task.FromException<OperatorComments>(failure);
            }

            var sent = comments.Comments.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            if (Comments.Comments.FirstOrDefault(c => !sent.Contains(c.Id)) is { } dropped)
            {
                return Task.FromException<OperatorComments>(new InvalidOperationException($"The worker may not drop comment '{dropped.Id}'."));
            }

            var stored = Comments.Comments.ToDictionary(c => c.Id, StringComparer.Ordinal);
            if (Revoked && comments.Comments.Any(c => $"comment:{c.Id}" != _executingTarget && stored[c.Id] != c))
            {
                return Task.FromException<OperatorComments>(new InvalidOperationException(NotGranted));
            }

            var failed = !string.IsNullOrEmpty(comments.SyncError) && (comments.SyncedAt is null || comments.SyncedAt == Comments.SyncedAt);
            var answered = failed || (Comments.SyncRequestedAt is { } at && comments.SyncedAt is { } synced
                && DateTimeOffset.Parse(synced, System.Globalization.CultureInfo.InvariantCulture) >= DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture));
            Comments = comments with
            {
                Comments = [.. comments.Comments.Select(c => stored.ContainsKey(c.Id) ? c : c with { FetchedAt = FetchedAt })],
                SyncedAt = comments.SyncedAt ?? Comments.SyncedAt,
                SyncRequestedAt = answered ? null : Comments.SyncRequestedAt,
                SyncError = comments.SyncError ?? (comments.SyncedAt is not null && comments.SyncedAt != Comments.SyncedAt ? null : Comments.SyncError),
            };
            return Task.FromResult(Comments);
        }
    }

    public bool Revoked { get; set; }

    public Func<OperatorProjectState, bool> RefuseOnProject { get; init; } = _ => false;

    private bool OnlyPublicationChanged(OperatorProjectState project)
        => _stored.TryGetValue(project.Id, out var stored) && (project with { Publication = stored.Publication }) == stored;

    private static string SceneList(OperatorScene scene) => $"{scene.Id}|{scene.Order}|{scene.Locked}";

    public string VoiceSamplesDirectory { get; set; } = "/nonexistent/scout-voice-samples";

    public Exception? VoiceSamplesFailure { get; set; }

    public Task<string> GetVoiceSamplesDirectoryAsync(CancellationToken ct)
        => VoiceSamplesFailure is null ? Task.FromResult(VoiceSamplesDirectory) : Task.FromException<string>(VoiceSamplesFailure);

    /// <summary>Every chat call, its name and its arguments as sent.</summary>
    public List<(string Name, string Arguments)> ChatCalls { get; } = [];

    /// <summary>When true, every chat call is refused with <see cref="NotGranted"/> and changes nothing.</summary>
    public bool ChatNotGranted { get; set; }

    public Task<OperatorProjectState> EditSceneTextAsync(string projectId, string sceneId, string text, CancellationToken ct)
        => ChatAsync("EditSceneText", $"{projectId}|{sceneId}|{text}", projectId,
            p => Queued(p) with { Scenes = [.. p.Scenes.Select(s => s.Id == sceneId ? s with { Narration = text, Status = OperatorPipeline.Planned } : s)] });

    public Task<OperatorProjectState> RegenerateNarrationAsync(string projectId, string sceneId, CancellationToken ct)
        => ChatAsync("RegenerateNarration", $"{projectId}|{sceneId}", projectId, Queued);

    public Task<OperatorProjectState> RegenerateSceneAsync(string projectId, string sceneId, CancellationToken ct)
        => ChatAsync("RegenerateScene", $"{projectId}|{sceneId}", projectId, Queued);

    public Task<OperatorProjectState> SetSceneLockedAsync(string projectId, string sceneId, bool locked, CancellationToken ct)
        => ChatAsync("SetSceneLocked", $"{projectId}|{sceneId}|{locked}", projectId,
            p => p with { Scenes = [.. p.Scenes.Select(s => s.Id == sceneId ? s with { Locked = locked } : s)] });

    public Task<OperatorProjectState> ReorderScenesAsync(string projectId, IReadOnlyList<string> sceneIds, CancellationToken ct)
        => ChatAsync("ReorderScenes", $"{projectId}|{string.Join(",", sceneIds)}", projectId,
            p => p with { Scenes = [.. p.Scenes.Select(s => s with { Order = sceneIds.ToList().IndexOf(s.Id) + 1 }).OrderBy(s => s.Order)] });

    public Task<OperatorProjectState> ProduceProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct)
        => ChatAsync("ProduceProject", $"{projectId}|{narrationVoice}|{direction}|{motion}", projectId, Queued);

    public Task<OperatorProjectState> DelegateProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct)
        => ChatAsync("DelegateProject", $"{projectId}|{narrationVoice}|{direction}|{motion}", projectId, p => Queued(p) with { Delegated = true });

    public Task<OperatorProjectState> RequestPresenterAsync(string projectId, string feedback, string prompt, CancellationToken ct)
        => ChatAsync("RequestPresenter", $"{projectId}|{feedback}|{prompt}", projectId,
            p => p with { PresenterRequest = feedback.Length > 0 ? feedback : "first", PresenterRequestPrompt = prompt });

    private static OperatorProjectState Queued(OperatorProjectState project) => project with { Status = "production_queued" };

    private Task<OperatorProjectState> ChatAsync(
        string name, string arguments, string projectId, Func<OperatorProjectState, OperatorProjectState> change)
    {
        lock (_lock)
        {
            ChatCalls.Add((name, arguments));
            if (ChatNotGranted)
            {
                return Task.FromException<OperatorProjectState>(new InvalidOperationException(NotGranted));
            }

            if (!_stored.TryGetValue(projectId, out var project))
            {
                return Task.FromException<OperatorProjectState>(new InvalidOperationException($"Unknown project '{projectId}'."));
            }

            var changed = change(project);
            _stored[projectId] = changed;
            return Task.FromResult(changed);
        }
    }
}
