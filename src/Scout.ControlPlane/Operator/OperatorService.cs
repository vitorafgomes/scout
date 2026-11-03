using System.Globalization;
using System.Text.Json;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;

namespace Scout.ControlPlane.Operator;

/// <summary>
/// The single writer of the channel operator's state. The desktop edits the mandate, starts and
/// produces projects under the owner's live write grant of the YouTube feature, decides claims and
/// edits scenes; the worker reads the mandate, reports its readiness, and reads projects, takes
/// queued ones and saves its drafts under the same grant, each save and each take recorded in the
/// write log. The owner queues an assembly, edits the package metadata, confirms its media rights
/// and approves it. The worker can never mark a claim supported or waived, change an owner's decision,
/// change a project's topic or origin, reorder, lock, change a locked scene or replace the owner's
/// media, or confirm, approve or change the owner's metadata of a package. The owner publishes an approved
/// package and starts title and thumbnail experiments, each creating the YouTube actions it needs as approved by
/// the owner; the worker claims them one at a time and saves the publication's progress, never the owner's
/// settings or decisions. The desktop never receives a project's origin reference.
/// </summary>
public sealed class OperatorService
{
    /// <summary>Status of a project the desktop started and no worker has taken yet.</summary>
    public const string Queued = "queued";

    /// <summary>Origin of a project started on the desktop.</summary>
    public const string DesktopOrigin = "desktop";

    /// <summary>Origin of a project a chat request started through the worker.</summary>
    public const string ChatOrigin = "chat";

    /// <summary>Status of a project the owner sent to production and no worker has taken yet.</summary>
    public const string ProductionQueued = "production_queued";

    private const string Researching = "researching";
    private const string Scripting = "scripting";
    private const string ScriptReady = "script_ready";
    private const string Producing = "producing";
    private const string MediaReady = "media_ready";
    private const string Failed = "failed";
    private const string NotReported = "The Scout worker has not reported yet.";
    private const string NoLongerProducing = "The project is no longer producing.";
    private const string NoLongerAssembling = "The project is no longer assembling.";
    private const string AssemblyQueued = PackageRules.AssemblyQueued;
    private const string Assembling = PackageRules.Assembling;
    private const string Assembled = PackageRules.Assembled;
    private const string Approved = PackageRules.Approved;
    private const string ReadinessNotSaved = "Scout could not save the worker's readiness report in its data folder.";
    private const string DataFolderScope = "Scout's data folder";
    private const int DefaultListLimit = 50;
    private const int MaxListLimit = 200;
    private const int MaxWorkerListLimit = 5000;
    private const string Superseded = "superseded by a new publish";
    private const string ArmNotRun = "The experiment ended before this arm ran.";
    private const int MaxPillars = 5;
    private const int MaxPillarLength = 80;
    private const int MinTopicLength = 3;
    private const int MaxTopicLength = 200;
    private const int DefaultArmHours = 48;
    private const int MinArmHours = 24;
    private const int MaxArmHours = 168;
    private const int MaxVariants = 2;
    private const string Insert = "videos.insert";
    private const string Update = "videos.update";
    private const string SetThumbnail = "thumbnails.set";

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ResearchKeptFor = TimeSpan.FromDays(30);
    private static readonly string[] WorkerStatuses = [Researching, Scripting, ScriptReady, Producing, MediaReady, Assembling, Assembled, Failed];
    private static readonly string[] WorkerClaimStatuses = ["pending", "unsupported"];
    private static readonly string[] OwnerClaimStatuses = ["supported", "unsupported", "waived"];

    private readonly JsonFileOperatorStore _store;
    private readonly WriteGateService _gate;
    private readonly IWriteLog _writes;
    private readonly IActionQueue _actions;

    /// <summary>
    /// Build the service over its store, the write gate that holds the grant check and the approval queue, and the
    /// write log.
    /// </summary>
    public OperatorService(JsonFileOperatorStore store, WriteGateService gate, IWriteLog writes)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _actions = gate.Actions;
    }

    /// <summary>The saved mandate, or the empty one when the owner has not written it.</summary>
    public async Task<Mandate> GetMandateAsync(CancellationToken ct) =>
        await _store.ReadMandateAsync(ct).ConfigureAwait(false) ?? Mandate.Empty;

    /// <summary>Validate, trim and save the owner's mandate.</summary>
    /// <exception cref="OperatorValidationException">The objective or language is empty, or the pillars break their limits.</exception>
    public async Task<Mandate> SaveMandateAsync(Mandate mandate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mandate);

        var pillars = Clean(mandate.Pillars);
        var trimmed = new Mandate(
            mandate.Objective.Trim(),
            mandate.Audience.Trim(),
            pillars,
            mandate.Cadence.Trim(),
            mandate.Language.Trim(),
            mandate.VoiceAndStyle.Trim(),
            Clean(mandate.BlockedTopics),
            mandate.TargetMetric.Trim(),
            mandate.TargetValue.Trim(),
            DateTimeOffset.UtcNow);

        var errors = new List<string>();
        if (trimmed.Objective.Length == 0)
        {
            errors.Add("The objective is required.");
        }
        if (pillars.Count > MaxPillars)
        {
            errors.Add($"At most {MaxPillars} pillars.");
        }
        if (pillars.Any(p => p.Length > MaxPillarLength))
        {
            errors.Add($"Each pillar is at most {MaxPillarLength} characters.");
        }
        if (trimmed.Language.Length == 0)
        {
            errors.Add("The language is required.");
        }
        if (errors.Count > 0)
        {
            throw new OperatorValidationException(string.Join(" ", errors));
        }

        await _store.WriteMandateAsync(trimmed, ct).ConfigureAwait(false);
        return trimmed;
    }

    /// <summary>The reason a revoke or a disconnect of YouTube gives every action it cancels.</summary>
    public const string RevokedReason = "The owner revoked YouTube writes before this action ran.";

    /// <summary>The newest projects first; a <paramref name="limit"/> of 0 or less means 50, and at most 200 are returned.</summary>
    public Task<IReadOnlyList<OperatorProject>> ListProjectsAsync(int limit, CancellationToken ct) =>
        ListNewestAsync(limit, MaxListLimit, ct);

    /// <summary>One project, without its origin reference.</summary>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public async Task<OperatorProject> GetProjectAsync(string id, CancellationToken ct)
    {
        var projects = await _store.ReadProjectsAsync(ct).ConfigureAwait(false);
        return ForDesktop(projects.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
            ?? throw ProjectNotFound(id));
    }

    /// <summary>Queue a project on <paramref name="topic"/> from the desktop under the owner's write grant.</summary>
    /// <exception cref="OperatorValidationException">The topic is not 3 to 200 characters after trimming.</exception>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public async Task<OperatorProject> StartProjectAsync(string topic, CancellationToken ct)
    {
        var project = NewProject(topic, Queued, DesktopOrigin, originReference: null);
        await RequireGrantAsync(project.Topic, ct).ConfigureAwait(false);
        await _store.UpdateProjectsAsync(projects => { projects.Add(project); return project; }, ct)
            .ConfigureAwait(false);
        return project;
    }

    /// <summary>Record the owner's decision on one claim.</summary>
    /// <exception cref="OperatorValidationException">The status is not supported, unsupported or waived, or a waiver has no note.</exception>
    /// <exception cref="OperatorNotFoundException">The project or the claim does not exist.</exception>
    public async Task<OperatorProject> DecideClaimAsync(
        string projectId,
        string claimId,
        string status,
        string? note,
        CancellationToken ct)
    {
        if (!OwnerClaimStatuses.Contains(status, StringComparer.Ordinal))
        {
            throw new OperatorValidationException("A claim is decided as supported, unsupported or waived.");
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (status == "waived" && trimmedNote is null)
        {
            throw new OperatorValidationException("A waived claim needs a note saying why.");
        }

        var decided = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    var claims = project.Claims.ToList();
                    var claimIndex = claims.FindIndex(c => string.Equals(c.Id, claimId, StringComparison.Ordinal));
                    if (claimIndex < 0)
                    {
                        throw new OperatorNotFoundException($"Unknown claim '{claimId}' in project '{projectId}'.");
                    }

                    var now = DateTimeOffset.UtcNow;
                    claims[claimIndex] = claims[claimIndex] with { Status = status, Note = trimmedNote, DecidedAt = now };
                    projects[index] = project with { Claims = claims, UpdatedAt = now };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(decided);
    }

    /// <summary>
    /// Save the worker's draft of a project under the owner's write grant. An empty id creates a
    /// project of origin chat that keeps <paramref name="originReference"/>; an existing project
    /// keeps its topic, origin, creation time and reference.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">A status, claim id or topic the operator does not accept.</exception>
    /// <exception cref="OperatorTamperingException">
    /// A change only the owner or the desktop may make, any save on a project the owner queued for production or
    /// assembly again, a save of an approved project that changes more than its publication and the research it may
    /// clear, a save of producing, media ready or any scene change on a project that is no longer producing, or a save
    /// of assembling, assembled or any package change on a project that is no longer assembling or whose scenes it
    /// changes. A project created more than 30 days ago may have its topic candidates and its claims' sources cleared.
    /// </exception>
    /// <exception cref="OperatorNotFoundException">No project has the id.</exception>
    public async Task<OperatorProject> SaveProjectFromWorkerAsync(
        OperatorProject project,
        string? originReference,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        var refusal = await GrantRefusalAsync(project.Id, ct).ConfigureAwait(false);
        if (refusal is not null && (string.IsNullOrEmpty(project.Id) || !await RunningOnYouTubeAsync(project.Id, ct).ConfigureAwait(false)))
        {
            throw new OperatorWriteRefusedException(refusal);
        }

        if (!WorkerStatuses.Contains(project.Status, StringComparer.Ordinal) && project.Status != Approved)
        {
            throw NotAWorkerStatus(project.Status);
        }

        ValidateClaims(project.Claims);
        SceneRules.Validate(project.Scenes);
        if (project.Package is not null)
        {
            PackageRules.Validate(project.Package);
        }

        if (string.IsNullOrEmpty(project.Id))
        {
            if (project.Status == Approved)
            {
                throw NotAWorkerStatus(project.Status);
            }
            if (project.Status is Assembling or Assembled || project.Package is not null)
            {
                throw new OperatorTamperingException(NoLongerAssembling);
            }

            RefuseOwnerStatuses(project.Claims, []);
            var created = NewProject(project.Topic, project.Status, ChatOrigin, originReference) with
            {
                Candidates = project.Candidates,
                Script = project.Script,
                Claims = project.Claims.Select(c => c with { DecidedAt = null }).ToList(),
                Error = project.Error,
                Scenes = SceneRules.MergeFromWorker(string.Empty, [], project.Scenes),
            };
            await _store.UpdateProjectsAsync(projects => { projects.Add(created); return created; }, ct)
                .ConfigureAwait(false);
            await LogAsync(created.Id, $"saved {created.Status}", approvedBy: null, ct).ConfigureAwait(false);
            return created;
        }

        string? ended = null;
        var saved = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, project.Id);
                    var stored = projects[index];
                    if (!string.Equals(stored.Topic, project.Topic, StringComparison.Ordinal)
                        || !string.Equals(stored.Origin, project.Origin, StringComparison.Ordinal))
                    {
                        throw new OperatorTamperingException(
                            $"The worker may not change the topic or origin of project '{stored.Id}'.");
                    }

                    var mayClear = stored.CreatedAt < DateTimeOffset.UtcNow - ResearchKeptFor;
                    var claims = MergeClaims(stored.Claims, project.Claims, mayClear);
                    var scenes = SceneRules.MergeFromWorker(stored.Id, stored.Scenes, project.Scenes);
                    var package = PackageRules.MergeFromWorker(stored.Id, stored.Package, project.Package);
                    var publication = PublicationRules.MergeFromWorker(stored.Id, stored.Publication, project.Publication);
                    var next = stored with
                    {
                        Status = project.Status,
                        Candidates = project.Candidates,
                        Script = project.Script,
                        Claims = claims,
                        Error = project.Error,
                        Scenes = scenes,
                        Package = package,
                        Publication = publication,
                    };
                    if (refusal is not null && !OnlyWhatARunningActionDid(stored, next))
                    {
                        throw new OperatorWriteRefusedException(refusal);
                    }

                    if (stored.Status == Approved || project.Status == Approved)
                    {
                        if (stored.Status != Approved)
                        {
                            throw NotAWorkerStatus(project.Status);
                        }
                        if (!OnlyPublicationOrClearedResearch(stored, next, mayClear))
                        {
                            throw new OperatorTamperingException(NoLongerAssembling);
                        }
                    }
                    else
                    {
                        var productionSave = project.Status is Producing or MediaReady || !scenes.SequenceEqual(stored.Scenes);
                        var assemblySave = project.Status is Assembling or Assembled || !PackageRules.Same(package, stored.Package);
                        if (assemblySave
                                ? stored.Status != Assembling || productionSave
                                : stored.Status == AssemblyQueued)
                        {
                            throw new OperatorTamperingException(NoLongerAssembling);
                        }
                        if (stored.Status == ProductionQueued || (stored.Status != Producing && productionSave))
                        {
                            throw new OperatorTamperingException(NoLongerProducing);
                        }
                        if (project.Status == Assembled)
                        {
                            PackageRules.RequireFiles(MediaDirectoryOf(stored.Id), package);
                        }
                    }

                    if (stored.Publication?.Experiment is { Status: PublicationRules.Running } running
                        && publication?.Experiment is { Status: not PublicationRules.Running })
                    {
                        ended = running.Id;
                    }

                    projects[index] = next with { UpdatedAt = DateTimeOffset.UtcNow };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        await LogAsync(saved.Id, $"saved {saved.Status}", approvedBy: null, ct).ConfigureAwait(false);
        if (ended is not null)
        {
            var prefix = $"{saved.Id}:{ended}:";
            await CancelAsync(
                    (action, approved) => action.Target.StartsWith(prefix, StringComparison.Ordinal)
                        && !ReferenceEquals(action, approved.LastOrDefault(a => a.Target == prefix + PublicationRules.Control)),
                    ArmNotRun,
                    ct)
                .ConfigureAwait(false);
        }

        return saved;
    }

    /// <summary>One project for the worker under the owner's write grant, with the owner's claim decisions.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public async Task<OperatorProject> GetProjectForWorkerAsync(string id, CancellationToken ct)
    {
        if (await GrantRefusalAsync(id, ct).ConfigureAwait(false) is { } refusal && !await RunningOnYouTubeAsync(id, ct).ConfigureAwait(false))
        {
            throw new OperatorWriteRefusedException(refusal);
        }

        return await GetProjectAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The newest projects first for the worker under the owner's write grant; a <paramref name="limit"/> of 0 or less
    /// means 50, and at most 5000 are returned, so the worker's data hygiene reaches older projects.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public async Task<IReadOnlyList<OperatorProject>> ListProjectsForWorkerAsync(int limit, CancellationToken ct)
    {
        await RequireGrantAsync("operator projects", ct).ConfigureAwait(false);
        return await ListNewestAsync(limit, MaxWorkerListLimit, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<OperatorProject>> ListNewestAsync(int limit, int max, CancellationToken ct)
    {
        var take = limit <= 0 ? DefaultListLimit : Math.Min(limit, max);
        var projects = await _store.ReadProjectsAsync(ct).ConfigureAwait(false);
        return Enumerable.Reverse(projects).Take(take).Select(ForDesktop).ToList();
    }

    /// <summary>
    /// Under the owner's write grant take, in one step so two callers never take the same project,
    /// the oldest queued project and move it to researching; else the oldest project queued for
    /// production and move it to producing; else the oldest project queued for assembly and move it
    /// and its package to assembling; else the oldest project left in researching, scripting,
    /// producing or assembling with no save for 15 minutes, back in researching, producing or
    /// assembling. The take is recorded in the write log. Null when none waits, and then the
    /// projects file is not written.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public async Task<OperatorProject?> NextQueuedProjectAsync(CancellationToken ct)
    {
        await RequireGrantAsync("next queued project", ct).ConfigureAwait(false);
        if (Next(await _store.ReadProjectsAsync(ct).ConfigureAwait(false), DateTimeOffset.UtcNow).Index < 0)
        {
            return null;
        }

        var taken = await _store.UpdateProjectsAsync<OperatorProject?>(
                projects =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var (index, status) = Next(projects, now);
                    if (index < 0)
                    {
                        return null;
                    }

                    var package = projects[index].Package;
                    projects[index] = projects[index] with
                    {
                        Status = status,
                        UpdatedAt = now,
                        Package = status == Assembling && package is not null ? package with { Status = Assembling } : package,
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        if (taken is not null)
        {
            await LogAsync(taken.Id, $"saved {taken.Status}", approvedBy: null, ct).ConfigureAwait(false);
        }

        return taken;
    }

    /// <summary>
    /// Queue a script-ready, media-ready, or failed project that has a script, for production under
    /// the owner's write grant, once the owner decided every claim; the project's error is cleared.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">The project has no script to produce, or claims still pending.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public async Task<OperatorProject> ProduceProjectAsync(string id, CancellationToken ct)
    {
        await RequireGrantAsync(id, ct).ConfigureAwait(false);
        var produced = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, id);
                    var project = projects[index];
                    if (project.Status is not (ScriptReady or MediaReady) && !(project.Status == Failed && project.Script is not null))
                    {
                        throw new OperatorValidationException(
                            $"Only a project with its script ready, its media ready, or a failed one with a script goes to production; this one is {project.Status}.");
                    }

                    var pending = project.Claims.Count(c => c.Status == "pending");
                    if (pending > 0)
                    {
                        throw new OperatorValidationException(pending == 1
                            ? "1 claim is still pending; decide it before production."
                            : $"{pending} claims are still pending; decide them before production.");
                    }

                    projects[index] = project with { Status = ProductionQueued, Error = null, UpdatedAt = DateTimeOffset.UtcNow };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(produced);
    }

    /// <summary>
    /// Put the project's scenes in the owner's order; <paramref name="sceneIds"/> names every scene once,
    /// and the first is order 1. An order that changes the video sends an assembled or approved project
    /// back to media ready without its rights and approval, and one queued for assembly or assembling
    /// back to queued for assembly; the order it already has changes neither.
    /// </summary>
    /// <exception cref="OperatorValidationException">The ids are not exactly the project's scenes.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>.</exception>
    public async Task<OperatorProject> ReorderScenesAsync(string projectId, IReadOnlyList<string> sceneIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sceneIds);

        var reordered = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    if (!sceneIds.Order(StringComparer.Ordinal).SequenceEqual(
                            project.Scenes.Select(s => s.Id).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                    {
                        throw new OperatorValidationException("The new order must name every scene of the project exactly once.");
                    }

                    var scenes = sceneIds
                        .Select((id, i) => project.Scenes.First(s => s.Id == id) with { Order = i + 1 })
                        .ToList();
                    var moved = !sceneIds.SequenceEqual(project.Scenes.OrderBy(s => s.Order).Select(s => s.Id), StringComparer.Ordinal);
                    projects[index] = (moved ? AfterSceneEdit(project, null) : project) with { Scenes = scenes, UpdatedAt = DateTimeOffset.UtcNow };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(reordered);
    }

    /// <summary>
    /// Lock or unlock one scene; a locked scene is never changed by the worker or regenerated. A lock
    /// does not change the video, so the project's status, rights and approval stay as they are.
    /// </summary>
    /// <exception cref="OperatorNotFoundException">The project or the scene does not exist.</exception>
    public Task<OperatorProject> SetSceneLockedAsync(string projectId, string sceneId, bool locked, CancellationToken ct) =>
        ChangeSceneAsync(projectId, sceneId, scene => scene with { Locked = locked }, projectStatus: null, changesVideo: false, ct);

    /// <summary>
    /// Send one unlocked scene back to planned and the project to production: the scene keeps the
    /// owner's media and loses its generated files and error. The package goes back to assembled
    /// without its rights and approval, or to queued for assembly when it was queued or assembling.
    /// </summary>
    /// <exception cref="OperatorValidationException">The scene is locked.</exception>
    /// <exception cref="OperatorNotFoundException">The project or the scene does not exist.</exception>
    public Task<OperatorProject> RegenerateSceneAsync(string projectId, string sceneId, CancellationToken ct) =>
        ChangeSceneAsync(
            projectId,
            sceneId,
            scene =>
            {
                if (scene.Locked)
                {
                    throw new OperatorValidationException($"Scene '{scene.Id}' is locked; unlock it before regenerating it.");
                }

                var ownAudio = scene.AudioSource == SceneRules.Owner;
                var ownVisual = scene.VisualSource == SceneRules.Owner;
                return scene with
                {
                    Status = SceneRules.Planned,
                    Error = null,
                    AudioFile = ownAudio ? scene.AudioFile : null,
                    VisualFile = ownVisual ? scene.VisualFile : null,
                    DurationMs = ownAudio ? scene.DurationMs : 0,
                };
            },
            ProductionQueued,
            changesVideo: true,
            ct);

    /// <summary>
    /// Use the owner's own audio or visual for one scene. The file must already be in the project's
    /// media directory. The scene is ready once it has a visual file, or a text card production chose,
    /// and an audio file, or no audio production chose; a scene production has not made yet counts only
    /// files. The owner's audio leaves the duration unknown, 0. An assembled or approved project goes
    /// back to media ready without its rights and approval, and one queued for assembly or assembling
    /// back to queued for assembly, so a running assembly stops and the next one uses the new media.
    /// </summary>
    /// <exception cref="OperatorValidationException">The kind, the file name or the file is not acceptable.</exception>
    /// <exception cref="OperatorNotFoundException">The project or the scene does not exist.</exception>
    public Task<OperatorProject> AttachSceneMediaAsync(
        string projectId,
        string sceneId,
        string kind,
        string fileName,
        CancellationToken ct)
    {
        if (kind is not (SceneRules.Audio or SceneRules.Visual))
        {
            throw new OperatorValidationException("Media is attached as audio or visual.");
        }

        SceneRules.ValidateFileName(fileName, kind);
        var directory = MediaDirectoryOf(projectId);
        return ChangeSceneAsync(
            projectId,
            sceneId,
            scene =>
            {
                if (!File.Exists(Path.Combine(directory, fileName)))
                {
                    throw new OperatorValidationException(
                        $"'{fileName}' is not in the project's media folder; copy it there first.");
                }

                var produced = scene.Status == SceneRules.Ready;
                var attached = kind == SceneRules.Audio
                    ? scene with { AudioSource = SceneRules.Owner, AudioFile = fileName, DurationMs = 0 }
                    : scene with { VisualSource = SceneRules.Owner, VisualFile = fileName };
                var ready = (attached.VisualFile is not null || (produced && attached.VisualSource == SceneRules.TextCard))
                    && (attached.AudioFile is not null || (produced && attached.AudioSource == SceneRules.NoAudio));
                return ready ? attached with { Status = SceneRules.Ready, Error = null } : attached;
            },
            projectStatus: null,
            changesVideo: true,
            ct);
    }

    /// <summary>The absolute path of the project's media directory, created when missing.</summary>
    /// <exception cref="OperatorValidationException">The id is not a project id.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>.</exception>
    public async Task<string> GetMediaDirectoryAsync(string projectId, CancellationToken ct)
    {
        var directory = MediaDirectoryOf(projectId);
        await GetProjectAsync(projectId, ct).ConfigureAwait(false);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>The project's media directory for the worker under the owner's write grant.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">The id is not a project id.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>.</exception>
    public async Task<string> GetMediaDirectoryForWorkerAsync(string projectId, CancellationToken ct)
    {
        await RequireGrantAsync(projectId, ct).ConfigureAwait(false);
        return await GetMediaDirectoryAsync(projectId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Queue a project for assembly under the owner's write grant: one with its media ready, an
    /// assembled one, or a failed one, every scene of it ready. The project's and the package's
    /// error are cleared, and the package starts again: no files, checks, rights or approval, and the
    /// owner's metadata kept only when the owner edited it.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">The project's status, no scenes, or a scene not ready.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public async Task<OperatorProject> AssembleProjectAsync(string id, CancellationToken ct)
    {
        await RequireGrantAsync(id, ct).ConfigureAwait(false);
        var queued = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, id);
                    var project = projects[index];
                    if (project.Status is not (MediaReady or Assembled or Failed))
                    {
                        throw new OperatorValidationException(
                            $"Only a project with its media ready, an assembled one, or a failed one with every scene ready is assembled; this one is {project.Status}.");
                    }
                    if (project.Scenes.Count == 0)
                    {
                        throw new OperatorValidationException("The project has no scenes to assemble.");
                    }

                    var notReady = project.Scenes.Count(s => s.Status != SceneRules.Ready);
                    if (notReady > 0)
                    {
                        throw new OperatorValidationException(notReady == 1
                            ? "1 scene is not ready; regenerate it or attach its media before assembly."
                            : $"{notReady} scenes are not ready; regenerate them or attach their media before assembly.");
                    }

                    projects[index] = project with
                    {
                        Status = AssemblyQueued,
                        Error = null,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Package = PackageRules.Queue(project.Package),
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(queued);
    }

    /// <summary>Record the owner's confirmation that they hold the rights to every medium of an assembled package.</summary>
    /// <exception cref="OperatorValidationException">The project is not assembled.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public Task<OperatorProject> ConfirmMediaRightsAsync(string id, CancellationToken ct) =>
        ChangePackageAsync(
            id,
            (project, package) => project.Status == Assembled
                ? project with { Package = package with { RightsConfirmed = true } }
                : throw new OperatorValidationException(
                    $"Media rights are confirmed on an assembled package; this one is {project.Status}."),
            ct);

    /// <summary>
    /// Replace the package's title, description and tags with the owner's, trimmed, within YouTube's
    /// limits, and mark them the owner's; the metadata and discoverability checks are run again over
    /// them against the project's topic and the mandate's pillars. An approved package goes back to
    /// assembled and loses its approval.
    /// </summary>
    /// <exception cref="OperatorValidationException">The metadata breaks a limit, or the project is not assembled or approved.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public async Task<OperatorProject> EditPackageMetadataAsync(
        string id,
        string title,
        string description,
        IReadOnlyList<string> tags,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var trimmedTitle = (title ?? string.Empty).Trim();
        var trimmedDescription = (description ?? string.Empty).Trim();
        var trimmedTags = tags.Select(t => t.Trim()).ToList();
        PackageRules.ValidateMetadata(trimmedTitle, trimmedDescription, trimmedTags);
        var pillars = (await GetMandateAsync(ct).ConfigureAwait(false)).Pillars;
        return await ChangePackageAsync(
            id,
            (project, package) => project.Status is Assembled or Approved
                ? project with
                {
                    Status = Assembled,
                    Package = package with
                    {
                        Status = Assembled,
                        ApprovedAt = null,
                        Title = trimmedTitle,
                        Description = trimmedDescription,
                        Tags = trimmedTags,
                        Checks = PackageRules.WithMetadataChecks(
                            package.Checks, trimmedTitle, trimmedDescription, trimmedTags, project.Topic, pillars),
                        MetadataEdited = true,
                    },
                }
                : throw new OperatorValidationException(
                    $"Only the metadata of an assembled or approved package is edited; this one is {project.Status}."),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Approve an assembled package under the owner's write grant once the owner confirmed its media
    /// rights, no check failed and every claim is supported or waived; the approval is recorded in the
    /// write log as the owner's.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">The project is not assembled, or what still blocks the approval.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public async Task<OperatorProject> ApprovePackageAsync(string id, CancellationToken ct)
    {
        await RequireGrantAsync(id, ct).ConfigureAwait(false);
        var approved = await ChangePackageAsync(
                id,
                (project, package) =>
                {
                    if (project.Status != Assembled)
                    {
                        throw new OperatorValidationException($"Only an assembled package is approved; this one is {project.Status}.");
                    }

                    var blocks = new List<string>();
                    if (!package.RightsConfirmed)
                    {
                        blocks.Add("Confirm the media rights first.");
                    }

                    var failed = package.Checks.Count(c => c.Result == PackageRules.Fail);
                    if (failed > 0)
                    {
                        blocks.Add(failed == 1 ? "1 check failed." : $"{failed} checks failed.");
                    }

                    var open = project.Claims.Count(c => c.Status is not ("supported" or "waived"));
                    if (open > 0)
                    {
                        blocks.Add(open == 1 ? "1 claim is not supported or waived." : $"{open} claims are not supported or waived.");
                    }
                    blocks.AddRange(PackageRules.MetadataErrors(package.Title, package.Description, package.Tags));
                    if (blocks.Count > 0)
                    {
                        throw new OperatorValidationException(string.Join(" ", blocks));
                    }

                    return project with
                    {
                        Status = Approved,
                        Package = package with { Status = Approved, ApprovedAt = DateTimeOffset.UtcNow },
                    };
                },
                ct)
            .ConfigureAwait(false);
        await LogAsync(approved.Id, "approved package", "owner", ct).ConfigureAwait(false);
        return approved;
    }

    /// <summary>
    /// Publish an approved package under the owner's write grant with the owner's privacy (public, private or
    /// unlisted), optional publish time, in the future and only for a private video, and declarations. The
    /// publication becomes <c>publish_approved</c>, and the upload, and the thumbnail when the package has one, are
    /// queued as approved by the owner, who confirmed them in the desktop, each approval logged as the owner's. A
    /// project already on YouTube, queued for upload or uploading, or with a YouTube action running, is not published
    /// again; one whose upload failed before YouTube took it is, and its earlier approved actions are rejected as
    /// superseded so none of them blocks the new upload.
    /// </summary>
    /// <exception cref="OperatorValidationException">A setting outside those, or the project cannot be published now.</exception>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>.</exception>
    public async Task<OperatorProject> PublishPackageAsync(
        string projectId,
        string privacyStatus,
        DateTimeOffset? publishAt,
        bool madeForKids,
        bool syntheticMedia,
        CancellationToken ct)
    {
        if (!PublicationRules.PrivacyStatuses.Contains(privacyStatus, StringComparer.Ordinal))
        {
            throw new OperatorValidationException("A video is published as public, private or unlisted.");
        }
        if (publishAt is { } at && at <= DateTimeOffset.UtcNow)
        {
            throw new OperatorValidationException("The publish time must be in the future.");
        }
        if (publishAt is not null && privacyStatus != PublicationRules.Private)
        {
            throw new OperatorValidationException("A video with a publish time is uploaded as private; YouTube makes it public at that time.");
        }

        await RequireGrantAsync(projectId, ct).ConfigureAwait(false);
        var grantedAt = await _gate.GrantedAtAsync(FeatureCatalog.YouTubeId, ct).ConfigureAwait(false);
        if ((await _actions.ListPendingAsync(ct).ConfigureAwait(false))
            .Any(a => a.Status == PendingActionStatus.Executing && !Stale(a, grantedAt) && ProjectOf(a) == projectId))
        {
            throw new OperatorValidationException("A YouTube action of this project is running; wait for it to end.");
        }

        var published = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    if (project.Status != Approved || project.Package is not { Status: Approved })
                    {
                        throw new OperatorValidationException($"Only an approved package is published; this one is {project.Status}.");
                    }
                    if (project.Publication is { } before
                        && (before.VideoId is not null || before.Status != PublicationRules.Failed))
                    {
                        throw new OperatorValidationException(before.VideoId is not null
                            ? "The video of this project is already on YouTube."
                            : $"The video of this project is {before.Status}; wait for the upload to end.");
                    }

                    projects[index] = project with
                    {
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Publication = new Publication(
                            PublicationRules.PublishApproved,
                            null,
                            privacyStatus,
                            publishAt,
                            madeForKids,
                            syntheticMedia,
                            null,
                            null,
                            project.Publication?.Snapshots ?? [],
                            project.Publication?.Proposals ?? [],
                            project.Publication?.Experiment),
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);

        var title = published.Package!.Title;
        var when = publishAt is { } time
            ? $" as private, made public at {time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}"
            : $" as {privacyStatus}";
        List<(string, string, string)> actions = [(Insert, projectId, $"Upload \"{title}\" to YouTube{when}.")];
        if (published.Package.ThumbnailFile is not null)
        {
            actions.Add((SetThumbnail, projectId, $"Set the thumbnail of \"{title}\" on YouTube."));
        }

        await CancelAsync((action, _) => ProjectOf(action) == projectId, Superseded, ct).ConfigureAwait(false);
        await ApproveAsync(actions, ct).ConfigureAwait(false);
        return ForDesktop(published);
    }

    /// <summary>Record the owner's decision on a proposal the worker made: accepted or rejected.</summary>
    /// <exception cref="OperatorValidationException">The proposal is already decided.</exception>
    /// <exception cref="OperatorNotFoundException">The project or the proposal does not exist.</exception>
    public async Task<OperatorProject> DecideProposalAsync(string projectId, string proposalId, bool accept, CancellationToken ct)
    {
        var decided = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    var proposals = project.Publication?.Proposals.ToList() ?? [];
                    var at = proposals.FindIndex(p => string.Equals(p.Id, proposalId, StringComparison.Ordinal));
                    if (at < 0)
                    {
                        throw new OperatorNotFoundException($"Unknown proposal '{proposalId}' in project '{projectId}'.");
                    }
                    if (proposals[at].Status != PublicationRules.Proposed)
                    {
                        throw new OperatorValidationException($"Proposal '{proposalId}' is already {proposals[at].Status}.");
                    }

                    proposals[at] = proposals[at] with { Status = accept ? PublicationRules.Accepted : PublicationRules.Rejected };
                    projects[index] = project with
                    {
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Publication = project.Publication! with { Proposals = proposals },
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(decided);
    }

    /// <summary>
    /// Start a title or thumbnail experiment on a published or scheduled video under the owner's write grant: the
    /// kind of the newest accepted title or thumbnail proposal, a control arm with the package's title or thumbnail
    /// and one arm for each of the first two accepted proposals of that kind, which become <c>tested</c> so no later
    /// experiment uses them again, each live for
    /// <paramref name="armHours"/> hours, 24 to 168, 0 meaning 48. One approved action per arm, in order, then one
    /// that restores the control, each logged as the owner's approval; an arm action is claimed only once the arm
    /// before it has been live that long.
    /// </summary>
    /// <exception cref="OperatorValidationException">The arm hours, or what the video still lacks.</exception>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>.</exception>
    public async Task<OperatorProject> StartExperimentAsync(string projectId, int armHours, CancellationToken ct)
    {
        var hours = armHours == 0 ? DefaultArmHours : armHours;
        if (hours is < MinArmHours or > MaxArmHours)
        {
            throw new OperatorValidationException($"Each arm runs {MinArmHours} to {MaxArmHours} hours.");
        }

        await RequireGrantAsync(projectId, ct).ConfigureAwait(false);
        var started = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    var publication = project.Publication;
                    if (publication is not { VideoId: not null, Status: PublicationRules.Published or PublicationRules.Scheduled })
                    {
                        throw new OperatorValidationException(
                            $"Only a published or scheduled video is tested; this one is {publication?.Status ?? "not published"}.");
                    }
                    if (publication.Experiment is { Status: PublicationRules.Running })
                    {
                        throw new OperatorValidationException("An experiment is already running on this video.");
                    }

                    var accepted = publication.Proposals
                        .Where(p => p.Status == PublicationRules.Accepted && p.Kind is PublicationRules.Title or PublicationRules.Thumbnail)
                        .ToList();
                    if (accepted.Count == 0)
                    {
                        throw new OperatorValidationException("Accept a title or thumbnail proposal first.");
                    }

                    var kind = accepted[^1].Kind;
                    var control = kind == PublicationRules.Title ? project.Package?.Title : project.Package?.ThumbnailFile;
                    if (string.IsNullOrEmpty(control))
                    {
                        throw new OperatorValidationException($"The package has no {kind} to come back to.");
                    }

                    var used = accepted.Where(p => p.Kind == kind).Take(MaxVariants).ToList();
                    var variants = used
                        .Select((p, i) => new ExperimentArm(
                            $"variant-{(char)('a' + i)}", $"Variant {(char)('A' + i)}", kind == PublicationRules.Title ? p.Text : p.ThumbnailFile!, null, null, 0, 0, 0));
                    var experiment = new Experiment(
                        Guid.NewGuid().ToString("N"),
                        kind,
                        PublicationRules.Running,
                        [new ExperimentArm(PublicationRules.Control, "Control", control, null, null, 0, 0, 0), .. variants],
                        null,
                        null,
                        hours);
                    projects[index] = project with
                    {
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Publication = publication with
                        {
                            Experiment = experiment,
                            Proposals = [.. publication.Proposals.Select(p => used.Contains(p) ? p with { Status = PublicationRules.Tested } : p)],
                        },
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);

        var experiment = started.Publication!.Experiment!;
        var actions = experiment.Arms
            .Select(arm => ArmAction(projectId, experiment, arm, $"for {experiment.ArmHours} hours, {arm.Label} of a {experiment.Kind} test"))
            .Append(ArmAction(projectId, experiment, experiment.Arms[0], $"again when the {experiment.Kind} test ends"))
            .ToList();
        await ApproveAsync(actions, ct).ConfigureAwait(false);
        return ForDesktop(started);
    }

    /// <summary>
    /// Put the winner of a concluded experiment live for good under the owner's write grant: the winner becomes the
    /// package's title or thumbnail, so a later experiment's control and restore use it, the experiment becomes
    /// <c>adopted</c> so it is adopted once, and one action approved by the owner and logged as the owner's approval
    /// puts it on YouTube.
    /// </summary>
    /// <exception cref="OperatorValidationException">The experiment is not concluded with a winner.</exception>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>.</exception>
    public async Task<OperatorProject> AdoptExperimentWinnerAsync(string projectId, CancellationToken ct)
    {
        await RequireGrantAsync(projectId, ct).ConfigureAwait(false);
        ExperimentArm? winner = null;
        var adopted = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    var experiment = project.Publication?.Experiment;
                    if (experiment is { Status: PublicationRules.Adopted })
                    {
                        throw new OperatorValidationException("The winner of this experiment is already adopted.");
                    }

                    winner = experiment is { Status: PublicationRules.Concluded }
                        ? experiment.Arms.FirstOrDefault(a => a.Id == experiment.WinnerArmId)
                        : null;
                    if (winner is null || project.Package is not { } package)
                    {
                        throw new OperatorValidationException("Only a concluded experiment with a winner is adopted.");
                    }

                    projects[index] = project with
                    {
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Publication = project.Publication! with { Experiment = experiment! with { Status = PublicationRules.Adopted } },
                        Package = experiment!.Kind == PublicationRules.Title
                            ? package with { Title = winner.Value }
                            : package with { ThumbnailFile = winner.Value },
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);

        var tested = adopted.Publication!.Experiment!;
        await ApproveAsync([ArmAction(projectId, tested, winner!, $"for good, the winner of the {tested.Kind} test")], ct)
            .ConfigureAwait(false);
        return ForDesktop(adopted);
    }

    /// <summary>
    /// For the worker: claim the oldest YouTube action the owner approved that no write recorded yet, as executing,
    /// so no other caller gets it until its claim is 30 minutes old. An action waits while an older one of the same
    /// project is open, and an arm of a running experiment waits until the arm live before it has run its hours.
    /// An action approved, or claimed, before the owner's current grant is stale: it is never handed out again and
    /// never holds back its project. Null without the owner's YouTube grant.
    /// </summary>
    public async Task<PendingAction?> NextApprovedActionAsync(CancellationToken ct)
    {
        var decision = await _gate.RequestAsync(
                FeatureCatalog.YouTubeId, DataFolderScope, "next approved action", "Read the owner's approved actions", ct)
            .ConfigureAwait(false);
        if (decision.Kind != WriteDecisionKind.Allowed)
        {
            return null;
        }

        var grantedAt = await _gate.GrantedAtAsync(FeatureCatalog.YouTubeId, ct).ConfigureAwait(false);
        var projects = await _store.ReadProjectsAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return await _actions.ClaimNextAsync(
                FeatureCatalog.YouTubeId,
                ClaimLease,
                (action, open) => !Stale(action, grantedAt)
                    && ReferenceEquals(open.First(o => !Stale(o, grantedAt) && ProjectOf(o) == ProjectOf(action)), action)
                    && Due(action, projects, now),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// For the worker: the origin reference of the project an approved action acts on, the first part of its target;
    /// null when the project has none or no longer exists.
    /// </summary>
    public async Task<string?> OriginReferenceForWorkerAsync(PendingAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        var projects = await _store.ReadProjectsAsync(ct).ConfigureAwait(false);
        return projects.FirstOrDefault(p => string.Equals(p.Id, ProjectOf(action), StringComparison.Ordinal))?.OriginReference;
    }

    /// <summary>
    /// Once the owner revokes YouTube writes or disconnects YouTube: reject every approved YouTube action with
    /// <see cref="RevokedReason"/>, each logged, so no stale approval runs under a later grant, and end every running
    /// experiment <c>inconclusive</c> with that reason. An action a worker is executing is left to its record.
    /// </summary>
    public async Task CancelApprovedYouTubeActionsAsync(CancellationToken ct)
    {
        await CancelAsync((_, _) => true, RevokedReason, ct).ConfigureAwait(false);
        await _store.UpdateProjectsAsync<object?>(
                projects =>
                {
                    var changed = false;
                    for (var i = 0; i < projects.Count; i++)
                    {
                        if (projects[i].Publication is { Experiment.Status: PublicationRules.Running } publication)
                        {
                            projects[i] = projects[i] with
                            {
                                Publication = publication with
                                {
                                    Experiment = publication.Experiment! with { Status = PublicationRules.Inconclusive, Result = RevokedReason },
                                },
                            };
                            changed = true;
                        }
                    }

                    return changed ? projects.Count : null;
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Clear the topic candidates, drawn from other channels' videos, of every project created more than 30 days ago,
    /// as YouTube's developer policies require; it needs no grant, so it runs with writes off or only an API key set.
    /// The projects file is written only when a project had candidates to clear.
    /// </summary>
    public Task ClearOldResearchAsync(CancellationToken ct) =>
        _store.UpdateProjectsAsync<object?>(
            projects =>
            {
                var cutoff = DateTimeOffset.UtcNow - ResearchKeptFor;
                var cleared = 0;
                for (var i = 0; i < projects.Count; i++)
                {
                    if (projects[i].CreatedAt < cutoff && projects[i].Candidates.Count > 0)
                    {
                        projects[i] = projects[i] with { Candidates = [] };
                        cleared++;
                    }
                }

                return cleared > 0 ? cleared : null;
            },
            ct);

    /// <summary>
    /// Remove what Scout read from YouTube once the owner disconnects it: every publication's snapshots, every
    /// experiment arm's impressions, clicks and average view percentage, and every project's topic candidates and
    /// claim sources. The owner's content stays: scripts, scenes, media, packages, video ids, and the experiments'
    /// arms, times, winners and results. It needs no grant, which the disconnect takes away.
    /// </summary>
    public Task PurgeYouTubeDataAsync(CancellationToken ct) =>
        _store.UpdateProjectsAsync(
            projects =>
            {
                for (var i = 0; i < projects.Count; i++)
                {
                    projects[i] = projects[i] with
                    {
                        Candidates = [],
                        Claims = [.. projects[i].Claims.Select(c => c with { SourceUrls = [] })],
                        Publication = projects[i].Publication is { } publication
                            ? publication with
                            {
                                Snapshots = [],
                                Experiment = publication.Experiment is { } experiment
                                    ? experiment with
                                    {
                                        Arms = [.. experiment.Arms.Select(a => a with { Impressions = 0, Clicks = 0, AverageViewPercentage = 0 })],
                                    }
                                    : null,
                            }
                            : null,
                    };
                }

                return projects.Count;
            },
            ct);

    /// <summary>Keep the worker's latest readiness report, stamped with the time it arrived.</summary>
    /// <exception cref="OperatorStoreException">The report could not be saved; the earlier one stays.</exception>
    public async Task<Readiness> ReportReadinessAsync(Readiness readiness, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        var stamped = readiness with { ReportedAt = DateTimeOffset.UtcNow };
        try
        {
            await _store.WriteReadinessAsync(stamped, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OperatorStoreException(ReadinessNotSaved, ex);
        }

        return stamped;
    }

    /// <summary>The worker's latest readiness report, or an empty one saying the worker has not reported.</summary>
    public async Task<Readiness> GetReadinessAsync(CancellationToken ct) =>
        await _store.ReadReadinessAsync(ct).ConfigureAwait(false)
        ?? new Readiness(false, false, string.Empty, false, false, [NotReported]);

    private static (int Index, string Status) Next(IReadOnlyList<OperatorProject> projects, DateTimeOffset now)
    {
        var queued = FirstIndex(projects, p => p.Status == Queued);
        if (queued >= 0)
        {
            return (queued, Researching);
        }

        var production = FirstIndex(projects, p => p.Status == ProductionQueued);
        if (production >= 0)
        {
            return (production, Producing);
        }

        var assembly = FirstIndex(projects, p => p.Status == AssemblyQueued);
        if (assembly >= 0)
        {
            return (assembly, Assembling);
        }

        var stale = FirstIndex(projects, p => p.Status is Researching or Scripting or Producing or Assembling && now - p.UpdatedAt > StaleAfter);
        return stale < 0 ? (-1, string.Empty) : (stale, projects[stale].Status is Producing or Assembling ? projects[stale].Status : Researching);
    }

    private static int FirstIndex(IReadOnlyList<OperatorProject> projects, Func<OperatorProject, bool> match)
    {
        for (var i = 0; i < projects.Count; i++)
        {
            if (match(projects[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static OperatorValidationException NotAWorkerStatus(string status) =>
        new($"Status '{status}' is not one the worker may set: {string.Join(", ", WorkerStatuses)}.");

    private static string ProjectOf(PendingAction action) => action.Target.Split(':')[0];

    private static bool Stale(PendingAction action, DateTimeOffset? grantedAt) =>
        grantedAt is { } since
        && (action.Status == PendingActionStatus.Executing ? action.ClaimedAt : action.DecidedAt) < since;

    private static bool Due(PendingAction action, IReadOnlyList<OperatorProject> projects, DateTimeOffset now)
    {
        var parts = action.Target.Split(':');
        var experiment = parts.Length == 3
            ? projects.FirstOrDefault(p => p.Id == parts[0])?.Publication?.Experiment
            : null;
        if (experiment is not { Status: PublicationRules.Running } || experiment.Id != parts[1])
        {
            return true;
        }

        var live = experiment.Arms.FirstOrDefault(a => a.StartedAt is not null && a.EndedAt is null);
        return live is null || live.StartedAt!.Value.AddHours(experiment.ArmHours) <= now;
    }

    private static (string Action, string Target, string Summary) ArmAction(string projectId, Experiment experiment, ExperimentArm arm, string when) =>
        experiment.Kind == PublicationRules.Title
            ? (Update, $"{projectId}:{experiment.Id}:{arm.Id}", $"Change the video's title to \"{arm.Value}\" {when}.")
            : (SetThumbnail, $"{projectId}:{experiment.Id}:{arm.Id}", $"Change the video's thumbnail to {arm.Value} {when}.");

    private static bool OnlyPublicationOrClearedResearch(OperatorProject stored, OperatorProject next, bool mayClear)
    {
        var expected = stored with
        {
            Claims = [.. stored.Claims.Select(c => mayClear && next.Claims.FirstOrDefault(n => n.Id == c.Id) is { SourceUrls.Count: 0 }
                ? c with { SourceUrls = [] }
                : c)],
        };
        return JsonSerializer.Serialize(expected with { Publication = null })
            == JsonSerializer.Serialize(next with { Publication = null });
    }

    private async Task CancelAsync(Func<PendingAction, IReadOnlyList<PendingAction>, bool> match, string reason, CancellationToken ct)
    {
        foreach (var action in await _actions.RejectApprovedAsync(FeatureCatalog.YouTubeId, match, reason, ct).ConfigureAwait(false))
        {
            await _writes.AppendAsync(
                    new WriteLogEntry(DateTimeOffset.UtcNow, action.FeatureId, action.Action, action.Target, $"rejected: {reason}", "owner", action.Id),
                    ct)
                .ConfigureAwait(false);
        }
    }

    private async Task ApproveAsync(IReadOnlyList<(string Action, string Target, string Summary)> actions, CancellationToken ct)
    {
        foreach (var action in await _actions.EnqueueApprovedAsync(FeatureCatalog.YouTubeId, actions, ct).ConfigureAwait(false))
        {
            await _writes.AppendAsync(
                    new WriteLogEntry(
                        DateTimeOffset.UtcNow,
                        action.FeatureId,
                        action.Action,
                        action.Target,
                        WriteGateService.ApprovedOutcome,
                        "owner",
                        action.Id),
                    ct)
                .ConfigureAwait(false);
        }
    }

    private static OperatorProject ForDesktop(OperatorProject project) => project with { OriginReference = null };

    private static OperatorProject AfterSceneEdit(OperatorProject project, string? projectStatus) =>
        project with
        {
            Status = projectStatus ?? project.Status switch
            {
                Assembled or Approved => MediaReady,
                AssemblyQueued or Assembling => AssemblyQueued,
                _ => project.Status,
            },
            Package = PackageRules.AfterSceneEdit(project.Package),
        };

    private static OperatorProject NewProject(string topic, string status, string origin, string? originReference)
    {
        var trimmed = (topic ?? string.Empty).Trim();
        if (trimmed.Length is < MinTopicLength or > MaxTopicLength)
        {
            throw new OperatorValidationException(
                $"The topic must be {MinTopicLength} to {MaxTopicLength} characters.");
        }

        var now = DateTimeOffset.UtcNow;
        return new OperatorProject(
            Guid.NewGuid().ToString("N"),
            trimmed,
            status,
            origin,
            now,
            now,
            [],
            null,
            [],
            OriginReference: string.IsNullOrWhiteSpace(originReference) ? null : originReference);
    }

    private static List<string> Clean(IReadOnlyList<string> values) =>
        values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();

    private static int IndexOf(List<OperatorProject> projects, string id)
    {
        var index = projects.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        return index >= 0 ? index : throw ProjectNotFound(id);
    }

    private static OperatorNotFoundException ProjectNotFound(string id) => new($"Unknown project '{id}'.");

    private static void ValidateClaims(IReadOnlyList<Claim> claims)
    {
        if (claims.Any(c => string.IsNullOrWhiteSpace(c.Id)))
        {
            throw new OperatorValidationException("Every claim needs an id.");
        }
        if (claims.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != claims.Count)
        {
            throw new OperatorValidationException("Claim ids must be unique within a project.");
        }

        var unknown = claims.FirstOrDefault(c =>
            !WorkerClaimStatuses.Contains(c.Status, StringComparer.Ordinal)
            && !OwnerClaimStatuses.Contains(c.Status, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new OperatorValidationException($"Claim '{unknown.Id}' has an unknown status '{unknown.Status}'.");
        }
    }

    private static void RefuseOwnerStatuses(IReadOnlyList<Claim> incoming, IReadOnlyList<Claim> stored)
    {
        foreach (var claim in incoming)
        {
            var before = stored.FirstOrDefault(c => string.Equals(c.Id, claim.Id, StringComparison.Ordinal));
            if (!WorkerClaimStatuses.Contains(claim.Status, StringComparer.Ordinal)
                && !string.Equals(before?.Status, claim.Status, StringComparison.Ordinal))
            {
                throw new OperatorTamperingException(
                    $"Only the owner marks a claim {claim.Status}; claim '{claim.Id}' was not.");
            }
        }
    }

    private static List<Claim> MergeClaims(IReadOnlyList<Claim> stored, IReadOnlyList<Claim> incoming, bool mayClearSources)
    {
        foreach (var decided in stored.Where(c => c.DecidedAt is not null))
        {
            var sent = incoming.FirstOrDefault(c => string.Equals(c.Id, decided.Id, StringComparison.Ordinal));
            if (sent is null
                || !string.Equals(sent.Text, decided.Text, StringComparison.Ordinal)
                || !string.Equals(sent.Status, decided.Status, StringComparison.Ordinal)
                || !string.Equals(sent.Note, decided.Note, StringComparison.Ordinal)
                || !string.Equals(sent.Risk, decided.Risk, StringComparison.Ordinal)
                || !(sent.SourceUrls.SequenceEqual(decided.SourceUrls, StringComparer.Ordinal)
                    || (mayClearSources && sent.SourceUrls.Count == 0)))
            {
                throw new OperatorTamperingException(
                    $"Claim '{decided.Id}' carries the owner's decision; the worker may not change or drop it.");
            }
        }

        RefuseOwnerStatuses(incoming, stored);
        return incoming
            .Select(c => c with
            {
                DecidedAt = stored.FirstOrDefault(s => string.Equals(s.Id, c.Id, StringComparison.Ordinal))?.DecidedAt,
            })
            .ToList();
    }

    private string MediaDirectoryOf(string projectId) =>
        Guid.TryParseExact(projectId, "N", out _)
            ? Path.Combine(_store.MediaRoot, projectId)
            : throw new OperatorValidationException($"'{projectId}' is not a project id.");

    private async Task<OperatorProject> ChangeSceneAsync(
        string projectId,
        string sceneId,
        Func<Scene, Scene> change,
        string? projectStatus,
        bool changesVideo,
        CancellationToken ct)
    {
        var changed = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    var scenes = project.Scenes.ToList();
                    var sceneIndex = scenes.FindIndex(s => string.Equals(s.Id, sceneId, StringComparison.Ordinal));
                    if (sceneIndex < 0)
                    {
                        throw new OperatorNotFoundException($"Unknown scene '{sceneId}' in project '{projectId}'.");
                    }

                    scenes[sceneIndex] = change(scenes[sceneIndex]);
                    projects[index] = (changesVideo ? AfterSceneEdit(project, projectStatus) : project) with
                    {
                        Scenes = scenes,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(changed);
    }

    private async Task<OperatorProject> ChangePackageAsync(
        string projectId,
        Func<OperatorProject, VideoPackage, OperatorProject> change,
        CancellationToken ct)
    {
        var changed = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    var package = project.Package
                        ?? throw new OperatorValidationException($"Project '{projectId}' has no package yet; assemble it first.");
                    projects[index] = change(project, package) with { UpdatedAt = DateTimeOffset.UtcNow };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(changed);
    }

    private Task LogAsync(string projectId, string outcome, string? approvedBy, CancellationToken ct) =>
        _writes.AppendAsync(
            new WriteLogEntry(
                DateTimeOffset.UtcNow,
                FeatureCatalog.YouTubeId,
                DataFolderScope,
                projectId,
                outcome,
                approvedBy,
                ActionId: null),
            ct);

    private async Task RequireGrantAsync(string target, CancellationToken ct)
    {
        if (await GrantRefusalAsync(target, ct).ConfigureAwait(false) is { } refusal)
        {
            throw new OperatorWriteRefusedException(refusal);
        }
    }

    private async Task<string?> GrantRefusalAsync(string target, CancellationToken ct)
    {
        var decision = await _gate.RequestAsync(
                FeatureCatalog.YouTubeId,
                DataFolderScope,
                target,
                "Save the channel operator's state",
                ct)
            .ConfigureAwait(false);
        return decision.Kind == WriteDecisionKind.Allowed ? null : decision.Reason;
    }

    private async Task<bool> RunningOnYouTubeAsync(string projectId, CancellationToken ct) =>
        (await _actions.ListPendingAsync(ct).ConfigureAwait(false))
            .Any(a => a.Status == PendingActionStatus.Executing && a.FeatureId == FeatureCatalog.YouTubeId && ProjectOf(a) == projectId);

    private static bool OnlyWhatARunningActionDid(OperatorProject stored, OperatorProject next)
    {
        if (stored.Publication is not { } before || next.Publication is not { } after)
        {
            return false;
        }

        var expected = before with
        {
            Status = after.Status,
            VideoId = after.VideoId,
            UploadedAt = after.UploadedAt,
            Error = after.Error,
            Experiment = before.Experiment is { } experiment && after.Experiment is { } sent
                ? experiment with { Arms = [.. experiment.Arms.Zip(sent.Arms, (a, s) => a with { StartedAt = s.StartedAt, EndedAt = s.EndedAt })] }
                : before.Experiment,
        };
        return JsonSerializer.Serialize(stored with { Publication = expected }) == JsonSerializer.Serialize(next);
    }
}
