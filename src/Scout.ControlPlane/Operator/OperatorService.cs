using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;

namespace Scout.ControlPlane.Operator;

/// <summary>
/// The single writer of the channel operator's state. The desktop edits the mandate, starts and
/// produces projects under the owner's live write grant of the YouTube feature, decides claims and
/// edits scenes; the worker reads the mandate, reports its readiness, and reads projects, takes
/// queued ones and saves its drafts under the same grant, each save and each take recorded in the
/// write log. A project the worker creates for a chat request keeps the format and the direction the request gave; no
/// later save of the worker sets a project's format, direction, narration voice, motion or delegation. The owner queues
/// an assembly, edits the package metadata, confirms its media rights and approves it. Only through the chat calls,
/// under the same grant, the worker edits a scene's text, regenerates a scene or its narration, locks, unlocks or
/// reorders scenes, and produces or delegates a project, each the owner's own operation and logged via chat when it
/// changed the project. Its saves never mark a claim supported or waived, change an owner's decision, a project's topic
/// or origin or a locked scene, or replace the owner's media, and it never confirms, approves or edits a package.
/// A project of the owner's own video waits for the owner's recording, attached with no grant, before the worker takes
/// it to transcribe; the worker sets only the recording's length, while it transcribes. A project of an animated character
/// asks for its first presenter when its script is ready, and the owner asks for others; the worker takes each presenter
/// job before any production and its save adds at most one presenter version, the status going back to the one it had.
/// The owner publishes an approved package and starts title and thumbnail experiments, each creating the YouTube
/// actions it needs as approved by the owner; the worker claims them one at a time and saves the publication's
/// progress, never the owner's settings or decisions. The desktop never receives a project's origin reference.
/// </summary>
/// <remarks>
/// Build the service over its store, the write gate that holds the grant check and the approval queue, the
/// write log, and the logger of a chat change the write log could not record.
/// </remarks>
public sealed class OperatorService(JsonFileOperatorStore store, WriteGateService gate, IWriteLog writes, ILogger<OperatorService>? logger = null)
    : IDisposable
{
    /// <summary>Status of a project the desktop started and no worker has taken yet.</summary>
    public const string Queued = "queued";

    /// <summary>Origin of a project started on the desktop.</summary>
    public const string DesktopOrigin = "desktop";

    /// <summary>Origin of a project a chat request started through the worker.</summary>
    public const string ChatOrigin = "chat";

    /// <summary>Status of a project the owner sent to production and no worker has taken yet.</summary>
    public const string ProductionQueued = "production_queued";

    /// <summary>Format of a video narrated over pictures Scout makes: the image service, Openverse or a text card.</summary>
    public const string GeneratedPictures = "generated_pictures";

    /// <summary>Format of a video narrated over the owner's own pictures, one attached per scene.</summary>
    public const string OwnPictures = "own_pictures";

    /// <summary>Format of a video that is the owner's own recording, transcribed on this machine, every scene a clip of it.</summary>
    public const string OwnVideo = "own_video";

    /// <summary>Format of a video narrated by one cartoon character, the presenter picture, whose mouth moves with the narration.</summary>
    public const string AnimatedCharacter = "animated_character";

    private const string Researching = "researching";
    private const string Scripting = "scripting";
    private const string RecordingWaiting = "recording_waiting";
    private const string Transcribing = "transcribing";
    private const string Recording = SceneRules.Recording;
    private const string ScriptReady = "script_ready";
    private const string Producing = "producing";
    private const string MediaReady = "media_ready";
    private const string Failed = "failed";
    private const string Presenting = "presenting";
    private const string Background = "background";
    private const string NotReported = "The Scout worker has not reported yet.";
    private const string NoLongerProducing = "The project is no longer producing.";
    private const string NoLongerAssembling = "The project is no longer assembling.";
    private const string NoLongerTranscribing = "The project is no longer transcribing; the worker's transcript was not saved.";
    private const string NoLongerPresenting = "The project is no longer presenting.";
    private const string OwnerCopyPrefix = "owner-";
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
    private const int MaxNarrationVoiceLength = 80;
    private const string NarrationVoiceMarks = "_:.#-";
    private const string MalformedNarrationVoice = "A narration voice is an id of at most 80 letters, digits, _ : . # or -.";
    private const int MaxDirectionLength = 1000;
    private const string LongDirection = "A direction is at most 1000 characters.";
    private const string UnknownFormat = "The format is generated_pictures, own_pictures, own_video or animated_character.";
    private const string UnknownDefaultFormat = "The default format is generated_pictures, own_pictures, own_video or animated_character.";
    private const string OwnVideoFromChat = "A video of your own recording is started in Scout Desktop, Production, where you attach the recording.";
    private const string OwnVideoTakesNoMedia = "A project of your own video shows the recording in every scene; it takes no pictures or narration.";
    private const string OwnVideoMakesNothingAgain = "A project of your own video has no narration or pictures to make again; edit the scene's text to change its captions.";
    private const int MaxSceneTextLength = 700;
    private const string SceneTextLength = "A scene's text is 1 to 700 characters.";
    private const string GentleMotion = "gentle";
    private const string UnknownMotion = "Motion is still, gentle or dynamic.";
    private static readonly string[] Motions = ["still", GentleMotion, "dynamic"];
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
    private static readonly TimeSpan ClaimLease = WriteGateService.ClaimLease;
    private static readonly TimeSpan ResearchKeptFor = TimeSpan.FromDays(30);
    private static readonly string[] WorkerStatuses = [Researching, Scripting, Transcribing, ScriptReady, Producing, MediaReady, Assembling, Assembled, Failed];
    private static readonly string[] WorkerClaimStatuses = ["pending", "unsupported"];
    private static readonly string[] OwnerClaimStatuses = ["supported", "unsupported", "waived"];

    private readonly JsonFileOperatorStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly WriteGateService _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly IWriteLog _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    private readonly IActionQueue _actions = gate.Actions;
    private readonly SemaphoreSlim _projectActionsGate = new(1, 1);
    private readonly ILogger<OperatorService> _logger = logger ?? NullLogger<OperatorService>.Instance;

    /// <summary>Release the gate that keeps a project's deletion and the queueing of its approved actions apart.</summary>
    public void Dispose() => _projectActionsGate.Dispose();

    /// <summary>The saved mandate, or the empty one when the owner has not written it; a default format Scout does not make reads as generated pictures.</summary>
    public async Task<Mandate> GetMandateAsync(CancellationToken ct)
    {
        var mandate = await _store.ReadMandateAsync(ct).ConfigureAwait(false) ?? Mandate.Empty;
        return MakesFormat(mandate.DefaultFormat) ? mandate : mandate with { DefaultFormat = GeneratedPictures };
    }

    /// <summary>Validate, trim and save the owner's mandate.</summary>
    /// <exception cref="OperatorValidationException">The objective or language is empty, the pillars break their limits, or the default format is not one Scout makes.</exception>
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
            DateTimeOffset.UtcNow)
        {
            DefaultFormat = string.IsNullOrWhiteSpace(mandate.DefaultFormat) ? GeneratedPictures : mandate.DefaultFormat.Trim(),
        };

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
        if (!MakesFormat(trimmed.DefaultFormat))
        {
            errors.Add(UnknownDefaultFormat);
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

    /// <summary>Why an action the owner approved is rejected when its approval could not be written to the write log.</summary>
    public const string ApprovalNotLogged = "its approval could not be logged; approve it again";

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

    /// <summary>
    /// Queue a project on <paramref name="topic"/> from the desktop under the owner's write grant, in
    /// <paramref name="format"/>, trimmed, empty meaning the mandate's default format, with the owner's
    /// <paramref name="direction"/>, cleaned like a produce's; the format never changes after. A project of the owner's
    /// own video waits for its recording instead of being queued.
    /// </summary>
    /// <exception cref="OperatorValidationException">The topic is not 3 to 200 characters after trimming, the format is not one Scout makes, or the direction is over 1000 characters.</exception>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> StartProjectAsync(string topic, string format, string direction, CancellationToken ct) =>
        StartProjectAsync(topic, format, direction, string.Empty, ct);

    /// <summary>
    /// <see cref="StartProjectAsync(string, string, string, CancellationToken)"/> with the owner's
    /// <paramref name="presenter"/> description, cleaned like a direction.
    /// </summary>
    /// <exception cref="OperatorValidationException">As the start without a presenter, or the presenter is over 300 characters.</exception>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public async Task<OperatorProject> StartProjectAsync(string topic, string format, string direction, string presenter, CancellationToken ct)
    {
        var project = NewProject(topic, Queued, DesktopOrigin, originReference: null) with
        {
            Format = await FormatOrDefaultAsync(format, ct).ConfigureAwait(false),
            Direction = DirectionOf(direction),
            Presenter = PresenterOf(presenter),
        };
        project = project.Format == OwnVideo ? project with { Status = RecordingWaiting } : project;
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
    /// keeps its topic, origin, creation time and reference. A media-ready save of a delegated project whose every scene
    /// is ready is stored queued for assembly, its package started again, so the worker's next take assembles it. The save
    /// that makes a project of an animated character script ready with no presenter version and no presenter picture asks
    /// for its first presenter. A save of a project taken for its presenter job takes only the job's outcome: at most one
    /// new presenter version, made current unless the job was the automatic first one and the project already has a
    /// presenter picture, the oldest version no pointer keeps dropped past 10 and its files deleted after the store holds
    /// the change, the project's error, and the status the project had before the job.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">A status, claim id, topic or presenter version the operator does not accept.</exception>
    /// <exception cref="OperatorTamperingException">
    /// A change only the owner or the desktop may make, any save on a project the owner queued for production or
    /// assembly again, a save of an approved project that changes more than its publication and the research it may
    /// clear, a save of producing, media ready or any scene change on a project that is no longer producing, or a save
    /// of assembling, assembled or any package change on a project that is no longer assembling or whose scenes it
    /// changes, a save of transcribing on a project that is no longer transcribing, a save of presenting on a project that
    /// is no longer presenting, or a presenter job's save that keeps the request, adds more than one version or changes
    /// one. A project created more than 30 days ago may have its topic candidates and its claims' sources cleared.
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

        if (!WorkerStatuses.Contains(project.Status, StringComparer.Ordinal) && project.Status is not (Approved or Presenting))
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
            if (project.Status is Approved or Presenting)
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
                Format = await FormatOrDefaultAsync(project.Format, ct).ConfigureAwait(false),
                Direction = DirectionOf(project.Direction),
            };
            if (created.Format == OwnVideo)
            {
                throw new OperatorValidationException(OwnVideoFromChat);
            }

            await _store.UpdateProjectsAsync(projects => { projects.Add(created); return created; }, ct)
                .ConfigureAwait(false);
            await LogAsync(created.Id, $"saved {created.Status}", approvedBy: null, ct).ConfigureAwait(false);
            return created;
        }

        string? ended = null;
        var forgotten = new List<string>();
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

                    if (stored.Status == Presenting)
                    {
                        if (refusal is not null)
                        {
                            throw new OperatorWriteRefusedException(refusal);
                        }

                        projects[index] = PresenterSaved(stored, project, forgotten);
                        return projects[index];
                    }

                    if (project.Status == Presenting)
                    {
                        throw new OperatorTamperingException(NoLongerPresenting);
                    }

                    if (project.Status == Transcribing && stored.Status != Transcribing)
                    {
                        throw new OperatorTamperingException(NoLongerTranscribing);
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
                        RecordingMs = stored.Status == Transcribing ? project.RecordingMs : stored.RecordingMs,
                        PresenterFile = stored.PresenterFile ?? WorkerPresenterFile(stored, project.PresenterFile),
                    };
                    if (project.Status == ScriptReady && stored.Status != ScriptReady && stored.Format == AnimatedCharacter
                        && stored.PresenterVersions.Count == 0 && next.PresenterFile is null && stored.PresenterRequest.Length == 0)
                    {
                        next = next with { PresenterRequest = FirstPresenter };
                    }

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

                    var handOn = stored.Delegated && next.Status == MediaReady && EverySceneReady(next.Scenes);
                    projects[index] = next with
                    {
                        Status = handOn ? AssemblyQueued : next.Status,
                        Package = handOn ? PackageRules.Queue(next.Package) : next.Package,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        await LogAsync(saved.Id, $"saved {saved.Status}", approvedBy: null, ct).ConfigureAwait(false);
        DeleteForgotten(saved.Id, forgotten);
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
    /// the oldest queued project and move it to researching, or to transcribing when it is the owner's own
    /// video; else the oldest project with a presenter request that is not researching, scripting, transcribing,
    /// producing, assembling or presenting, and move it to presenting, keeping the status it goes back to; else the oldest
    /// project queued for production and move it to producing; else the oldest
    /// project queued for assembly and move it and its package to assembling; else the oldest project left
    /// in researching, scripting, transcribing, producing, assembling or presenting with no save for 15 minutes, back in
    /// researching, transcribing, producing, assembling or presenting. A project waiting for its recording is never
    /// taken. The take is recorded in the write log. Null when none waits, and then the projects file is
    /// not written.
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
                        PresenterReturnStatus = status == Presenting && projects[index].Status != Presenting
                            ? projects[index].Status
                            : projects[index].PresenterReturnStatus,
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
    /// Queue a script-ready, media-ready, assembled, approved, or failed project that has a script, for production under
    /// the owner's write grant, once the owner decided every claim; the project's error is cleared, its narration
    /// voice becomes <paramref name="narrationVoice"/>, trimmed, empty meaning Scout's choice, its direction
    /// <paramref name="direction"/>, trimmed with every control character but a newline made a space, and its motion
    /// <paramref name="motion"/>, empty meaning gentle.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">The narration voice is not an id, the direction is over 1000 characters, the motion is not a preset, the project has no script to produce, or claims still pending.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public Task<OperatorProject> ProduceProjectAsync(string id, string narrationVoice, string direction, string motion, CancellationToken ct) =>
        ProduceProjectAsync(id, narrationVoice, direction, motion, string.Empty, ct);

    /// <summary>
    /// <see cref="ProduceProjectAsync(string, string, string, string, CancellationToken)"/> with the owner's
    /// <paramref name="presenter"/> description, cleaned like a direction; a blank one keeps the project's.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">As the produce without a presenter, or the presenter is over 300 characters.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public async Task<OperatorProject> ProduceProjectAsync(string id, string narrationVoice, string direction, string motion, string presenter, CancellationToken ct)
    {
        var voice = NarrationVoiceOf(narrationVoice);
        var owned = DirectionOf(direction);
        var preset = MotionOf(motion);
        var character = Kept(presenter, PresenterOf);
        await RequireGrantAsync(id, ct).ConfigureAwait(false);
        return await ProduceAsync(id, voice, owned, preset, character, ct).ConfigureAwait(false);
    }

    private async Task<OperatorProject> ProduceAsync(string id, string? voice, string? owned, string? preset, string? character, CancellationToken ct)
    {
        var produced = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, id);
                    var project = projects[index];
                    if (project.Status is not (ScriptReady or MediaReady or Assembled or Approved) && !(project.Status == Failed && project.Script is not null))
                    {
                        throw new OperatorValidationException(
                            $"Only a project with its script ready, its media ready, an assembled or approved one, or a failed one with a script goes to production; this one is {project.Status}.");
                    }

                    var pending = project.Claims.Count(c => c.Status == "pending");
                    if (pending > 0)
                    {
                        throw new OperatorValidationException(pending == 1
                            ? "1 claim is still pending; decide it before production."
                            : $"{pending} claims are still pending; decide them before production.");
                    }

                    projects[index] = project with
                    {
                        Status = ProductionQueued,
                        Error = null,
                        Package = project.Status is Assembled or Approved ? PackageRules.AfterSceneEdit(project.Package) : project.Package,
                        NarrationVoice = voice ?? project.NarrationVoice,
                        Direction = owned ?? project.Direction,
                        Motion = preset ?? project.Motion,
                        Presenter = character ?? project.Presenter,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(produced);
    }

    /// <summary>
    /// Delegate a script-ready, media-ready, or failed project that has a script, under the owner's write grant: the
    /// project is marked delegated and queued for its next reversible stage, assembly when its media is ready and every
    /// scene of it is ready and none lacks a narration or picture the worker could still make, else production, whatever claims are still pending; the project's error is cleared and its
    /// narration voice, direction and motion become <paramref name="narrationVoice"/>, <paramref name="direction"/>
    /// and <paramref name="motion"/> under the rules of <see cref="ProduceProjectAsync(string, string, string, string, CancellationToken)"/>. A text card counts as made
    /// in a project of the owner's own pictures, so its media-ready project goes straight to assembly, and a presenter scene
    /// counts as pictured once the project has its presenter picture, else as a text card.
    /// Delegation never approves or publishes anything.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">The narration voice is not an id, the direction is over 1000 characters, the motion is not a preset, the project's status, or a failed project without a script.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public Task<OperatorProject> DelegateProjectAsync(string id, string narrationVoice, string direction, string motion, CancellationToken ct) =>
        DelegateProjectAsync(id, narrationVoice, direction, motion, string.Empty, ct);

    /// <summary>
    /// <see cref="DelegateProjectAsync(string, string, string, string, CancellationToken)"/> with the owner's
    /// <paramref name="presenter"/> description, cleaned like a direction; a blank one keeps the project's.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">As the delegation without a presenter, or the presenter is over 300 characters.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public async Task<OperatorProject> DelegateProjectAsync(string id, string narrationVoice, string direction, string motion, string presenter, CancellationToken ct)
    {
        var voice = NarrationVoiceOf(narrationVoice);
        var owned = DirectionOf(direction);
        var preset = MotionOf(motion);
        var character = Kept(presenter, PresenterOf);
        await RequireGrantAsync(id, ct).ConfigureAwait(false);
        return await DelegateAsync(id, voice, owned, preset, character, ct).ConfigureAwait(false);
    }

    private async Task<OperatorProject> DelegateAsync(string id, string? voice, string? owned, string? preset, string? character, CancellationToken ct)
    {
        var readiness = await GetReadinessAsync(ct).ConfigureAwait(false);
        var delegated = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, id);
                    var project = projects[index];
                    if (project.Status is not (ScriptReady or MediaReady) && !(project.Status == Failed && project.Script is not null))
                    {
                        throw new OperatorValidationException(
                            $"Only a project with its script ready, its media ready, or a failed one with a script is delegated; this one is {project.Status}.");
                    }

                    var assemble = project.Status == MediaReady && EverySceneReady(project.Scenes)
                        && !project.Scenes.Any(s =>
                            (s.AudioSource == SceneRules.NoAudio && readiness.SpeechAvailable)
                            || (s.VisualSource == SceneRules.TextCard && readiness.ImagesAvailable && project.Format != OwnPictures)
                            || (s.VisualSource == SceneRules.Presenter && project.PresenterFile is null && readiness.ImagesAvailable));
                    projects[index] = project with
                    {
                        Status = assemble ? AssemblyQueued : ProductionQueued,
                        Package = assemble ? PackageRules.Queue(project.Package) : project.Package,
                        Delegated = true,
                        NarrationVoice = voice ?? project.NarrationVoice,
                        Direction = owned ?? project.Direction,
                        Motion = preset ?? project.Motion,
                        Presenter = character ?? project.Presenter,
                        Error = null,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(delegated);
    }

    private static string NarrationVoiceOf(string narrationVoice)
    {
        var voice = (narrationVoice ?? string.Empty).Trim();
        if (voice.Length > MaxNarrationVoiceLength
            || !voice.All(c => char.IsAsciiLetterOrDigit(c) || NarrationVoiceMarks.Contains(c, StringComparison.Ordinal)))
        {
            throw new OperatorValidationException(MalformedNarrationVoice);
        }

        return voice;
    }

    private static string OwnerText(string text) =>
        new string([.. (text ?? string.Empty).Select(c => c != '\n' && char.IsControl(c) ? ' ' : c)]).Trim();

    private static string DirectionOf(string direction)
    {
        var owned = OwnerText(direction);
        return owned.Length > MaxDirectionLength ? throw new OperatorValidationException(LongDirection) : owned;
    }

    private async Task<string> FormatOrDefaultAsync(string format, CancellationToken ct)
    {
        var chosen = (format ?? string.Empty).Trim();
        if (chosen.Length == 0)
        {
            return (await GetMandateAsync(ct).ConfigureAwait(false)).DefaultFormat;
        }

        return MakesFormat(chosen) ? chosen : throw new OperatorValidationException(UnknownFormat);
    }

    private static bool MakesFormat(string format) => format is GeneratedPictures or OwnPictures or OwnVideo or AnimatedCharacter;

    private static string MotionOf(string motion)
    {
        var preset = (motion ?? string.Empty).Trim();
        if (preset.Length == 0)
        {
            return GentleMotion;
        }

        return Motions.Contains(preset, StringComparer.Ordinal) ? preset : throw new OperatorValidationException(UnknownMotion);
    }

    private static bool EverySceneReady(IReadOnlyList<Scene> scenes) =>
        scenes.Count > 0 && scenes.All(s => s.Status == SceneRules.Ready);

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
        ChangeSceneAsync(projectId, sceneId, (_, scene) => scene with { Locked = locked }, _ => null, changesVideo: false, ct);

    /// <summary>
    /// Send one unlocked scene back to planned and the project to production: the scene keeps the
    /// owner's media and loses its generated files and error, and a stock picture becomes a text card
    /// without its credit. The package goes back to assembled
    /// without its rights and approval, or to queued for assembly when it was queued or assembling. A project of the
    /// owner's own video has nothing to make again and is refused.
    /// </summary>
    /// <exception cref="OperatorValidationException">The project is the owner's own video, or the scene is locked.</exception>
    /// <exception cref="OperatorNotFoundException">The project or the scene does not exist.</exception>
    public Task<OperatorProject> RegenerateSceneAsync(string projectId, string sceneId, CancellationToken ct) =>
        ChangeSceneAsync(
            projectId,
            sceneId,
            (project, scene) =>
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
                    VisualSource = scene.VisualSource == SceneRules.Stock ? SceneRules.TextCard : scene.VisualSource,
                    VisualFile = ownVisual ? scene.VisualFile : null,
                    VisualCredit = null,
                    DurationMs = ownAudio ? scene.DurationMs : 0,
                };
            },
            _ => ProductionQueued,
            changesVideo: true,
            ct,
            ownVideoRefusal: OwnVideoMakesNothingAgain);

    /// <summary>
    /// Replace one unlocked scene's text with the owner's <paramref name="text"/>, cleaned like a direction, and
    /// send its narration back to be made again: the scene is planned without its error, its audio, the owner's
    /// included, and its duration, and keeps its picture; the project goes to production and its package back to
    /// assembled without its rights and approval, or to queued for assembly when it was queued or assembling. In a
    /// project of the owner's own video the text is only the scene's captions: nothing else of the scene changes, nothing
    /// goes back to production, and an assembled or approved project goes back to media ready without its rights and
    /// approval, or, when the owner delegated it, to queued for assembly so the new captions are assembled without the
    /// owner. Scout's own state, so no grant is asked for and nothing is logged.
    /// </summary>
    /// <exception cref="OperatorValidationException">The text is not 1 to 700 characters, or the scene is locked.</exception>
    /// <exception cref="OperatorNotFoundException">The project or the scene does not exist.</exception>
    public async Task<OperatorProject> EditSceneTextAsync(string projectId, string sceneId, string text, CancellationToken ct)
    {
        var owned = OwnerText(text);
        if (owned.Length is 0 or > MaxSceneTextLength)
        {
            throw new OperatorValidationException(SceneTextLength);
        }

        return await ChangeSceneAsync(
            projectId,
            sceneId,
            (project, scene) =>
            {
                var locked = $"Scene '{scene.Id}' is locked; unlock it before editing its text.";
                if (project.Format == OwnVideo)
                {
                    return scene.Locked ? throw new OperatorValidationException(locked) : scene with { Narration = owned };
                }

                return WithoutNarration(scene, locked) with { Narration = owned };
            },
            project => project.Format != OwnVideo
                ? ProductionQueued
                : project.Delegated && project.Status is Assembled or Approved ? AssemblyQueued : null,
            changesVideo: true,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Send one unlocked scene's narration back to be made again, in the project's voice, keeping its text and its
    /// picture, under the rules of <see cref="EditSceneTextAsync"/>. A project of the owner's own video has no narration
    /// to make again and is refused.
    /// </summary>
    /// <exception cref="OperatorValidationException">The project is the owner's own video, or the scene is locked.</exception>
    /// <exception cref="OperatorNotFoundException">The project or the scene does not exist.</exception>
    public Task<OperatorProject> RegenerateNarrationAsync(string projectId, string sceneId, CancellationToken ct) =>
        ChangeSceneAsync(
            projectId,
            sceneId,
            (_, scene) => WithoutNarration(scene, $"Scene '{scene.Id}' is locked; unlock it before regenerating it."),
            _ => ProductionQueued,
            changesVideo: true,
            ct,
            ownVideoRefusal: OwnVideoMakesNothingAgain);

    private static Scene WithoutNarration(Scene scene, string locked) =>
        scene.Locked
            ? throw new OperatorValidationException(locked)
            : scene with
            {
                Status = SceneRules.Planned,
                Error = null,
                AudioSource = SceneRules.NoAudio,
                AudioFile = null,
                DurationMs = 0,
            };

    /// <summary>
    /// Use the owner's own audio or visual for one scene. The file must already be in the project's
    /// media directory. The scene is ready once it has a visual file, or a text card production chose,
    /// and an audio file, or no audio production chose; a scene production has not made yet counts only
    /// files. The owner's audio leaves the duration unknown, 0. An assembled or approved project goes
    /// back to media ready without its rights and approval, and one queued for assembly or assembling
    /// back to queued for assembly, so a running assembly stops and the next one uses the new media.
    /// The kind <c>background</c>, with no scene id and a picture already in the media directory, sets the
    /// project's background picture on a project whose script or media is ready, an assembled, approved or
    /// failed one, or one queued for assembly or assembling; it needs no grant and changes the status like a scene edit.
    /// The kind <c>recording</c>, with no scene id and a video already in the media directory under the name Scout
    /// Desktop gives its copy, starting <c>owner-</c>, sets the recording of a project of the owner's own video before
    /// Scout transcribes it, deletes the recording it replaces from the media directory, and queues the project for its
    /// transcription; it needs no grant. A project of the owner's own video takes no audio, visual or background.
    /// The kind <c>presenter</c>, with no scene id and a picture already in the media directory under the name Scout
    /// Desktop gives its copy, <c>owner-presenter-</c> and 12 hex digits, sets the presenter picture of a project of an
    /// animated character on the statuses a background is accepted on, as the owner's, deletes the picture it replaces from
    /// the media directory unless a presenter version keeps it, clears the mouth box and the preview unless the picture is
    /// the one already set, and changes the status like a scene edit;
    /// it needs no grant. A scene of an animated character takes the owner's visual as its backdrop, like a scene of
    /// generated pictures.
    /// </summary>
    /// <exception cref="OperatorValidationException">The kind, the file name, the file, a background's, a recording's or a presenter picture's scene id, the project's format or its status is not acceptable.</exception>
    /// <exception cref="OperatorNotFoundException">The project or the scene does not exist.</exception>
    public async Task<OperatorProject> AttachSceneMediaAsync(
        string projectId,
        string sceneId,
        string kind,
        string fileName,
        CancellationToken ct)
    {
        if (kind == Background)
        {
            return await AttachBackgroundAsync(projectId, sceneId, fileName, ct).ConfigureAwait(false);
        }

        if (kind == Recording)
        {
            return await AttachRecordingAsync(projectId, sceneId, fileName, ct).ConfigureAwait(false);
        }

        if (kind == SceneRules.Presenter)
        {
            return await AttachPresenterAsync(projectId, sceneId, fileName, ct).ConfigureAwait(false);
        }

        if (kind is not (SceneRules.Audio or SceneRules.Visual))
        {
            throw new OperatorValidationException("Media is attached as audio, visual, background or recording.");
        }

        SceneRules.ValidateFileName(fileName, kind);
        var directory = MediaDirectoryOf(projectId);
        return await ChangeSceneAsync(
            projectId,
            sceneId,
            (_, scene) =>
            {
                if (!File.Exists(Path.Combine(directory, fileName)))
                {
                    throw new OperatorValidationException(
                        $"'{fileName}' is not in the project's media folder; copy it there first.");
                }

                var produced = scene.Status == SceneRules.Ready;
                var attached = kind == SceneRules.Audio
                    ? scene with { AudioSource = SceneRules.Owner, AudioFile = fileName, DurationMs = 0 }
                    : scene with { VisualSource = SceneRules.Owner, VisualFile = fileName, VisualCredit = null };
                var ready = (attached.VisualFile is not null || (produced && attached.VisualSource == SceneRules.TextCard))
                    && (attached.AudioFile is not null || (produced && attached.AudioSource == SceneRules.NoAudio));
                return ready ? attached with { Status = SceneRules.Ready, Error = null } : attached;
            },
            _ => null,
            changesVideo: true,
            ct,
            ownVideoRefusal: OwnVideoTakesNoMedia).ConfigureAwait(false);
    }

    private async Task<OperatorProject> AttachBackgroundAsync(string projectId, string sceneId, string fileName, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(sceneId))
        {
            throw new OperatorValidationException("A background is attached to the project, not to a scene.");
        }

        SceneRules.ValidateFileName(fileName, SceneRules.Visual);
        var directory = MediaDirectoryOf(projectId);
        var attached = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    if (project.Format == OwnVideo)
                    {
                        throw new OperatorValidationException(OwnVideoTakesNoMedia);
                    }

                    if (project.Status is not (ScriptReady or MediaReady or Assembled or Approved or AssemblyQueued or Assembling or Failed))
                    {
                        throw new OperatorValidationException(
                            $"A background is attached to a project whose script or media is ready, an assembled or approved one, one queued for assembly or assembling, or a failed one; this one is {project.Status}.");
                    }

                    if (!File.Exists(Path.Combine(directory, fileName)))
                    {
                        throw new OperatorValidationException(
                            $"'{fileName}' is not in the project's media folder; copy it there first.");
                    }

                    projects[index] = AfterSceneEdit(project, null) with { BackgroundFile = fileName, UpdatedAt = DateTimeOffset.UtcNow };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(attached);
    }

    private async Task<OperatorProject> AttachRecordingAsync(string projectId, string sceneId, string fileName, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(sceneId))
        {
            throw new OperatorValidationException("A recording is attached to the project, not to a scene.");
        }

        SceneRules.ValidateFileName(fileName, SceneRules.Recording);
        if (!fileName.StartsWith(OwnerCopyPrefix, StringComparison.Ordinal))
        {
            throw new OperatorValidationException("A recording is attached from Scout Desktop, which names the copy for you.");
        }

        var directory = MediaDirectoryOf(projectId);
        string? previous = null;
        var attached = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    if (project.Format != OwnVideo)
                    {
                        throw new OperatorValidationException("Only a project of your own video takes a recording.");
                    }

                    if (!(project.Status is RecordingWaiting or Queued || (project.Status == Failed && project.Script is null)))
                    {
                        throw new OperatorValidationException(
                            $"A recording is attached before Scout transcribes it; this one is {project.Status}.");
                    }

                    if (!File.Exists(Path.Combine(directory, fileName)))
                    {
                        throw new OperatorValidationException(
                            $"'{fileName}' is not in the project's media folder; copy it there first.");
                    }

                    previous = project.RecordingFile;
                    projects[index] = project with
                    {
                        RecordingFile = fileName,
                        RecordingMs = 0,
                        Status = Queued,
                        Error = null,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        if (previous is not null && previous != fileName && Path.GetFileName(previous) == previous)
        {
            try
            {
                File.Delete(Path.Combine(directory, previous));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Scout attached the new recording but could not delete the one it replaced.");
            }
        }

        return ForDesktop(attached);
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

    /// <summary>
    /// Delete a project the owner no longer wants: it leaves the store and its media directory inside the operator
    /// media root is deleted, after the deletion is in the write log; a deletion that cannot be logged puts the project
    /// back and fails. Nothing leaves the process, so a video already on YouTube stays there. Refused while the
    /// project is researching, scripting, transcribing, producing, assembling, presenting or uploading, or while an approved or executing
    /// YouTube action targets it. A deletion and a publish, an experiment start or an adopted winner never overlap: each waits for
    /// the other to end, so an approved action never names a deleted project.
    /// </summary>
    /// <exception cref="OperatorValidationException">The project is running or has pending YouTube actions.</exception>
    /// <exception cref="OperatorStoreException">The project was removed but its media folder could not be deleted.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public async Task DeleteProjectAsync(string id, CancellationToken ct)
    {
        var directory = MediaDirectoryOf(id);
        using var held = await HoldProjectActionsAsync(ct).ConfigureAwait(false);
        if ((await _actions.ListOpenAsync(FeatureCatalog.YouTubeId, ct).ConfigureAwait(false)).Any(a => ProjectOf(a) == id))
        {
            throw new OperatorValidationException("The project has pending YouTube actions; finish or cancel them first.");
        }

        var at = -1;
        var removed = await _store.UpdateProjectsAsync(
                projects =>
                {
                    at = IndexOf(projects, id);
                    var project = projects[at];
                    if (project.Status is Researching or Scripting or Transcribing or Producing or Assembling or Presenting
                        || project.Publication?.Status == PublicationRules.Uploading)
                    {
                        throw new OperatorValidationException(
                            "A project that is researching, scripting, transcribing, producing, assembling, presenting or uploading cannot be deleted; wait for it to finish or fail.");
                    }

                    projects.RemoveAt(at);
                    return project;
                },
                ct)
            .ConfigureAwait(false);
        try
        {
            await LogAsync(id, "deleted project", "owner", ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _store.UpdateProjectsAsync(
                        projects =>
                        {
                            if (projects.Exists(p => string.Equals(p.Id, id, StringComparison.Ordinal)))
                            {
                                return null;
                            }

                            projects.Insert(Math.Min(at, projects.Count), removed);
                            return removed;
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scout could not log the deletion of project {ProjectId}, and putting the project back failed too.", id);
            }

            throw;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OperatorStoreException("Scout removed the project but could not delete its media folder.", ex);
        }
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

        OperatorProject? unpublished = null;
        using var held = await HoldProjectActionsAsync(ct).ConfigureAwait(false);
        var published = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    unpublished = project;
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

        await ApproveOrRestoreAsync(
                unpublished!,
                published,
                async () =>
                {
                    await CancelAsync((action, _) => ProjectOf(action) == projectId, Superseded, ct).ConfigureAwait(false);
                    await ApproveAsync(actions, ct).ConfigureAwait(false);
                })
            .ConfigureAwait(false);
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
        OperatorProject? untested = null;
        using var held = await HoldProjectActionsAsync(ct).ConfigureAwait(false);
        var started = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    untested = project;
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
        await ApproveOrRestoreAsync(untested!, started, () => ApproveAsync(actions, ct)).ConfigureAwait(false);
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
        OperatorProject? concluded = null;
        using var held = await HoldProjectActionsAsync(ct).ConfigureAwait(false);
        var adopted = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    concluded = project;
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
        await ApproveOrRestoreAsync(
                concluded!,
                adopted,
                () => ApproveAsync([ArmAction(projectId, tested, winner!, $"for good, the winner of the {tested.Kind} test")], ct))
            .ConfigureAwait(false);
        return ForDesktop(adopted);
    }

    /// <summary>
    /// For the worker: claim the oldest YouTube action the owner approved that no write recorded yet, as executing,
    /// so no other caller gets it until its claim is 30 minutes old. An action waits while an older one of the same
    /// project is open, and an arm of a running experiment waits until the arm live before it has run its hours.
    /// An action approved, or claimed, before the owner's current grant is stale: it is never handed out again and
    /// never holds back its project. Null without the owner's YouTube grant. An executing action whose claim lapsed and
    /// that is stale, or that has no grant to run under, is first rejected with <see cref="WriteGateService.NeverFinished"/>
    /// and logged, so it does not stay running forever; a claim that has not lapsed is left to its record.
    /// </summary>
    public async Task<PendingAction?> NextApprovedActionAsync(CancellationToken ct)
    {
        var grantedAtNow = await _gate.GrantedAtAsync(FeatureCatalog.YouTubeId, ct).ConfigureAwait(false);
        var lapsed = DateTimeOffset.UtcNow - ClaimLease;
        await _gate.RejectAndLogAsync(
                FeatureCatalog.YouTubeId,
                action => action.ClaimedAt <= lapsed && (grantedAtNow is null || Stale(action, grantedAtNow)),
                WriteGateService.NeverFinished,
                ct)
            .ConfigureAwait(false);
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
        return await _gate.ClaimUnrecordedAsync(
                FeatureCatalog.YouTubeId,
                (action, open) => !Stale(action, grantedAt)
                    && ReferenceEquals(open.First(o => !Stale(o, grantedAt) && QueueOf(o) == QueueOf(action)), action)
                    && Due(action, projects, now),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// For the worker: the origin reference of the project an approved action acts on, the first part of its target;
    /// null when the project has none or no longer exists, and for an action on a comment.
    /// </summary>
    public async Task<string?> OriginReferenceForWorkerAsync(PendingAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (ProjectOf(action) is not { } projectId)
        {
            return null;
        }

        var projects = await _store.ReadProjectsAsync(ct).ConfigureAwait(false);
        return projects.FirstOrDefault(p => string.Equals(p.Id, projectId, StringComparison.Ordinal))?.OriginReference;
    }

    /// <summary>
    /// Once the owner revokes YouTube writes or disconnects YouTube: reject every approved YouTube action with
    /// <see cref="RevokedReason"/>, each logged, so no stale approval runs under a later grant, and end every running
    /// experiment <c>inconclusive</c> with that reason. An action a worker is executing is left to its record. Every
    /// comment waiting on a YouTube action without a live one is reconciled, so a revoke retried after a failed comments
    /// write still repairs them.
    /// </summary>
    public async Task CancelApprovedYouTubeActionsAsync(CancellationToken ct)
    {
        await CancelAsync((_, _) => true, RevokedReason, ct).ConfigureAwait(false);
        var live = TargetsOf(await LiveCommentActionsAsync(ct).ConfigureAwait(false));
        await _store.UpdateCommentsAsync(state => CommentRules.Reconcile(state, live), ct).ConfigureAwait(false);
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
    /// as YouTube's developer policies require, reconciles every comment waiting on a YouTube action without a live one,
    /// and clears every comment fetched more than 30 days ago unless its action is live, with the ideas whose comment is
    /// gone; it needs no grant, so it runs with writes off or only an
    /// API key set. A file is written only when it had something to clear.
    /// </summary>
    /// <exception cref="OperatorStoreException">The comments file could not be written.</exception>
    public async Task ClearOldResearchAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - ResearchKeptFor;
        await _store.UpdateProjectsAsync<object?>(
                projects =>
                {
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
                ct)
            .ConfigureAwait(false);
        var live = TargetsOf(await LiveCommentActionsAsync(ct).ConfigureAwait(false));
        await _store.UpdateCommentsAsync(
                state =>
                {
                    var reconciled = CommentRules.Reconcile(state, live);
                    return CommentRules.Cleared(reconciled ?? state, cutoff, live) ?? reconciled;
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Remove what Scout read from YouTube once the owner disconnects it: every publication's snapshots, every
    /// experiment arm's impressions, clicks and average view percentage, and every project's topic candidates and
    /// claim sources. The owner's content stays: scripts, scenes, media, packages, video ids, and the experiments'
    /// arms, times, winners and results. Every comment, the sync's time and error, and every idea not used goes too; a
    /// used idea keeps only its id, status and project. It needs no grant, which the disconnect takes away.
    /// </summary>
    /// <exception cref="OperatorStoreException">The comments file could not be written.</exception>
    public async Task PurgeYouTubeDataAsync(CancellationToken ct)
    {
        await _store.UpdateProjectsAsync(
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
            ct).ConfigureAwait(false);
        await _store.UpdateCommentsAsync(CommentRules.Purged, ct).ConfigureAwait(false);
    }

    /// <summary>Keep the worker's latest readiness report, stamped with the time it arrived.</summary>
    /// <exception cref="OperatorStoreException">The report could not be saved; the earlier one stays.</exception>
    public async Task<Readiness> ReportReadinessAsync(Readiness readiness, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        foreach (var voice in readiness.Voices)
        {
            if (voice.SampleFile is not null)
            {
                ValidateSampleFileName(voice.SampleFile);
            }
        }

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

    private static void ValidateSampleFileName(string name)
    {
        try
        {
            SceneRules.ValidateFileName(name, SceneRules.Audio);
        }
        catch (OperatorValidationException)
        {
            throw new OperatorValidationException(VoiceSampleNotPlain);
        }

        if (!name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperatorValidationException(VoiceSampleNotPlain);
        }
    }

    private const string VoiceSampleNotPlain = "A voice sample is a plain .wav file name.";

    /// <summary>The worker's latest readiness report, or an empty one saying the worker has not reported.</summary>
    public async Task<Readiness> GetReadinessAsync(CancellationToken ct) =>
        await _store.ReadReadinessAsync(ct).ConfigureAwait(false)
        ?? new Readiness(false, false, string.Empty, false, false, [NotReported]);

    private static (int Index, string Status) Next(IReadOnlyList<OperatorProject> projects, DateTimeOffset now)
    {
        var queued = FirstIndex(projects, p => p.Status == Queued);
        if (queued >= 0)
        {
            return (queued, projects[queued].Format == OwnVideo ? Transcribing : Researching);
        }

        var presenter = FirstIndex(projects, p => p.PresenterRequest.Length > 0
            && p.Status is not (Researching or Scripting or Transcribing or Producing or Assembling or Presenting));
        if (presenter >= 0)
        {
            return (presenter, Presenting);
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

        var stale = FirstIndex(projects, p => p.Status is Researching or Scripting or Transcribing or Producing or Assembling or Presenting && now - p.UpdatedAt > StaleAfter);
        return stale < 0 ? (-1, string.Empty) : (stale, projects[stale].Status is Transcribing or Producing or Assembling or Presenting ? projects[stale].Status : Researching);
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

    private static string? ProjectOf(PendingAction action) =>
        action.Target.StartsWith(CommentRules.TargetPrefix, StringComparison.Ordinal) ? null : action.Target.Split(':')[0];

    private static string QueueOf(PendingAction action) => ProjectOf(action) ?? action.Target;

    internal static bool Stale(PendingAction action, DateTimeOffset? grantedAt) =>
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

    /// <summary>
    /// The approved or executing YouTube actions on a comment that are not stale against the owner's current grant.
    /// </summary>
    internal async Task<IReadOnlyList<PendingAction>> LiveCommentActionsAsync(CancellationToken ct)
    {
        var grantedAt = await _gate.GrantedAtAsync(FeatureCatalog.YouTubeId, ct).ConfigureAwait(false);
        return [.. (await _actions.ListOpenAsync(FeatureCatalog.YouTubeId, ct).ConfigureAwait(false))
            .Where(a => a.Target.StartsWith(CommentRules.TargetPrefix, StringComparison.Ordinal) && !Stale(a, grantedAt))];
    }

    internal static HashSet<string> TargetsOf(IEnumerable<PendingAction> actions) =>
        actions.Select(a => a.Target).ToHashSet(StringComparer.Ordinal);

    internal async Task<IReadOnlyList<PendingAction>> CancelAsync(
        Func<PendingAction, IReadOnlyList<PendingAction>, bool> match,
        string reason,
        CancellationToken ct)
    {
        var rejected = await _actions.RejectApprovedAsync(FeatureCatalog.YouTubeId, match, reason, ct).ConfigureAwait(false);
        foreach (var action in rejected)
        {
            await _writes.AppendAsync(
                    new WriteLogEntry(DateTimeOffset.UtcNow, action.FeatureId, action.Action, action.Target, $"rejected: {reason}", "owner", action.Id),
                    ct)
                .ConfigureAwait(false);
        }

        return rejected;
    }

    /// <summary>
    /// Queue <paramref name="actions"/> as approved by the owner and log each approval. When an approval cannot be logged
    /// the actions this call queued are rejected with <see cref="ApprovalNotLogged"/>, each rejection logged when the log
    /// takes it, so none runs without its entry.
    /// </summary>
    internal async Task<IReadOnlyList<PendingAction>> ApproveAsync(IReadOnlyList<(string Action, string Target, string Summary)> actions, CancellationToken ct)
    {
        var approved = await _actions.EnqueueApprovedAsync(FeatureCatalog.YouTubeId, actions, ct).ConfigureAwait(false);
        try
        {
            foreach (var action in approved)
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
        catch
        {
            try
            {
                await CancelAsync((queued, _) => approved.Any(a => a.Id == queued.Id), ApprovalNotLogged, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Scout could not log the approval of YouTube actions, and rejecting them or logging that rejection failed too; check the Approvals screen.");
            }

            throw;
        }

        return approved;
    }

    /// <summary>
    /// Run <paramref name="approve"/>, which queues the actions the owner approved for <paramref name="changed"/>. When it
    /// fails the project is returned to <paramref name="prior"/>, what it was before this call changed it, unless it was
    /// saved again meanwhile, so the owner can approve again; the failure is rethrown.
    /// </summary>
    private async Task ApproveOrRestoreAsync(OperatorProject prior, OperatorProject changed, Func<Task> approve)
    {
        try
        {
            await approve().ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _store.UpdateProjectsAsync(
                        projects =>
                        {
                            var index = projects.FindIndex(p => p.Id == changed.Id && p.UpdatedAt == changed.UpdatedAt);
                            return index < 0 ? null : projects[index] = prior;
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Scout could not queue the approved YouTube actions of project {ProjectId}, and returning the project to what it was failed too.",
                    changed.Id);
            }

            throw;
        }
    }

    private async Task<ProjectActionsHold> HoldProjectActionsAsync(CancellationToken ct)
    {
        await _projectActionsGate.WaitAsync(ct).ConfigureAwait(false);
        return new ProjectActionsHold(_projectActionsGate);
    }

    private readonly struct ProjectActionsHold(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
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
            Package = projectStatus == AssemblyQueued
                ? PackageRules.Queue(PackageRules.AfterSceneEdit(project.Package))
                : PackageRules.AfterSceneEdit(project.Package),
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
        Func<OperatorProject, Scene, Scene> change,
        Func<OperatorProject, string?> projectStatus,
        bool changesVideo,
        CancellationToken ct,
        string? ownVideoRefusal = null)
    {
        var changed = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    if (ownVideoRefusal is not null && project.Format == OwnVideo)
                    {
                        throw new OperatorValidationException(ownVideoRefusal);
                    }

                    var scenes = project.Scenes.ToList();
                    var sceneIndex = scenes.FindIndex(s => string.Equals(s.Id, sceneId, StringComparison.Ordinal));
                    if (sceneIndex < 0)
                    {
                        throw new OperatorNotFoundException($"Unknown scene '{sceneId}' in project '{projectId}'.");
                    }

                    if (project.Status == Presenting && projectStatus(project) is not null)
                    {
                        throw new OperatorValidationException(MakingPresenter);
                    }

                    scenes[sceneIndex] = change(project, scenes[sceneIndex]);
                    projects[index] = (changesVideo ? AfterSceneEdit(project, projectStatus(project)) : project) with
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

    internal Task LogAsync(string target, string outcome, string? approvedBy, CancellationToken ct) =>
        _writes.AppendAsync(
            new WriteLogEntry(
                DateTimeOffset.UtcNow,
                FeatureCatalog.YouTubeId,
                DataFolderScope,
                target,
                outcome,
                approvedBy,
                ActionId: null),
            ct);

    internal async Task RequireGrantAsync(string target, CancellationToken ct)
    {
        if (await GrantRefusalAsync(target, ct).ConfigureAwait(false) is { } refusal)
        {
            throw new OperatorWriteRefusedException(refusal);
        }
    }

    internal async Task<string?> GrantRefusalAsync(string target, CancellationToken ct)
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

    /// <summary>
    /// The absolute path of the folder that holds one sample per bundled narration voice, <c>voice-samples</c> inside the
    /// operator media root beside the project media directories, created when missing.
    /// </summary>
    public Task<string> GetVoiceSamplesDirectoryAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Path.Combine(_store.MediaRoot, VoiceSamplesFolder);
        Directory.CreateDirectory(directory);
        return Task.FromResult(directory);
    }

    /// <summary>The voice samples directory for the worker, which needs no grant because the samples are Scout's own data.</summary>
    public Task<string> GetVoiceSamplesDirectoryForWorkerAsync(CancellationToken ct) => GetVoiceSamplesDirectoryAsync(ct);

    internal const string VoiceSamplesFolder = "voice-samples";

    /// <summary>The owner's scene text edit asked from the chat: <see cref="EditSceneTextAsync"/> under the grant, logged via chat.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> EditSceneTextFromChatAsync(string projectId, string sceneId, string text, CancellationToken ct) =>
        ViaChatAsync(projectId, $"edited the text of scene {sceneId}", c => EditSceneTextAsync(projectId, sceneId, text, c), ct);

    /// <summary>The owner's narration remake asked from the chat: <see cref="RegenerateNarrationAsync"/> under the grant, logged via chat.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> RegenerateNarrationFromChatAsync(string projectId, string sceneId, CancellationToken ct) =>
        ViaChatAsync(projectId, $"asked for the narration of scene {sceneId} again", c => RegenerateNarrationAsync(projectId, sceneId, c), ct);

    /// <summary>The owner's scene remake asked from the chat: <see cref="RegenerateSceneAsync"/> under the grant, logged via chat.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> RegenerateSceneFromChatAsync(string projectId, string sceneId, CancellationToken ct) =>
        ViaChatAsync(projectId, $"asked for scene {sceneId} again", c => RegenerateSceneAsync(projectId, sceneId, c), ct);

    /// <summary>The owner's lock or unlock asked from the chat: <see cref="SetSceneLockedAsync"/> under the grant, logged via chat.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> SetSceneLockedFromChatAsync(string projectId, string sceneId, bool locked, CancellationToken ct) =>
        ViaChatAsync(projectId, $"{(locked ? "locked" : "unlocked")} scene {sceneId}", c => SetSceneLockedAsync(projectId, sceneId, locked, c), ct);

    /// <summary>The owner's scene order asked from the chat: <see cref="ReorderScenesAsync"/> under the grant, logged via chat.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> ReorderScenesFromChatAsync(string projectId, IReadOnlyList<string> sceneIds, CancellationToken ct) =>
        ViaChatAsync(projectId, "reordered the scenes", c => ReorderScenesAsync(projectId, sceneIds, c), ct);

    /// <summary>
    /// The owner's production asked from the chat: <see cref="ProduceProjectAsync(string, string, string, string, CancellationToken)"/> under the grant, logged via chat,
    /// where a blank voice, direction or motion keeps the project's own.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> ProduceProjectFromChatAsync(string id, string narrationVoice, string direction, string motion, CancellationToken ct) =>
        ViaChatAsync(id, "queued production", c => ProduceAsync(id, Kept(narrationVoice, NarrationVoiceOf), Kept(direction, DirectionOf), Kept(motion, MotionOf), null, c), ct);

    /// <summary>
    /// The owner's delegation asked from the chat: <see cref="DelegateProjectAsync(string, string, string, string, CancellationToken)"/> under the grant, logged via chat,
    /// where a blank voice, direction or motion keeps the project's own.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> DelegateProjectFromChatAsync(string id, string narrationVoice, string direction, string motion, CancellationToken ct) =>
        ViaChatAsync(id, "delegated", c => DelegateAsync(id, Kept(narrationVoice, NarrationVoiceOf), Kept(direction, DirectionOf), Kept(motion, MotionOf), null, c), ct);

    private static string? Kept(string value, Func<string, string> clean) =>
        string.IsNullOrWhiteSpace(value) ? null : clean(value);

    private async Task<OperatorProject> ViaChatAsync(
        string projectId,
        string what,
        Func<CancellationToken, Task<OperatorProject>> change,
        CancellationToken ct)
    {
        await RequireGrantAsync(projectId, ct).ConfigureAwait(false);
        var before = (await _store.ReadProjectsAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == projectId);
        var changed = await change(ct).ConfigureAwait(false);
        if (before is not null && Comparable(before) == Comparable(changed))
        {
            return changed;
        }

        try
        {
            await LogAsync(projectId, $"{what} via chat", approvedBy: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Scout changed a project from the chat but could not record the change in the write log.");
        }

        return changed;
    }

    private static string Comparable(OperatorProject project) =>
        JsonSerializer.Serialize(project with { UpdatedAt = default, OriginReference = null });

    private const int MaxPresenterLength = 300;
    private const double MinMouthSize = 0.02;
    private const string PresenterCopyPrefix = "owner-presenter-";
    private const string WorkerPresenterPrefix = "presenter-";
    private const int PresenterCopyHexLength = 12;
    private const string LongPresenter = "A presenter description is at most 300 characters.";
    private const string MalformedMouthBox = "A mouth box is four fractions of the picture, each between 0 and 1, at least 0.02 wide and high and inside the picture.";
    private const string NotAnimatedCharacter = "The project is not an animated character.";
    private const string PresenterOnAScene = "A presenter picture is attached to the project, not to a scene.";
    private const string PresenterNotNamedByDesktop = "A presenter picture is attached from Scout Desktop, which names the copy for you.";

    private static string PresenterOf(string presenter)
    {
        var owned = OwnerText(presenter);
        return owned.Length > MaxPresenterLength ? throw new OperatorValidationException(LongPresenter) : owned;
    }

    private static string? UnversionedPresenterFile(OperatorProject project) =>
        project.PresenterVersions.Any(v => v.File == project.PresenterFile) ? null : project.PresenterFile;

    private static bool WithinPicture(MouthBox box) =>
        new[] { box.X, box.Y, box.Width, box.Height }.All(v => double.IsFinite(v) && v is >= 0 and <= 1)
        && box.Width >= MinMouthSize
        && box.Height >= MinMouthSize
        && box.X + box.Width <= 1
        && box.Y + box.Height <= 1;

    private string? WorkerPresenterFile(OperatorProject stored, string? sent)
    {
        if (stored.Format != AnimatedCharacter || sent is null)
        {
            return null;
        }

        SceneRules.ValidateFileName(sent, SceneRules.Visual);
        return sent.StartsWith(WorkerPresenterPrefix, StringComparison.Ordinal) && File.Exists(Path.Combine(MediaDirectoryOf(stored.Id), sent))
            ? sent
            : null;
    }

    private async Task<OperatorProject> AttachPresenterAsync(string projectId, string sceneId, string fileName, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(sceneId))
        {
            throw new OperatorValidationException(PresenterOnAScene);
        }

        SceneRules.ValidateFileName(fileName, SceneRules.Visual);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.StartsWith(PresenterCopyPrefix, StringComparison.Ordinal)
            || stem.Length != PresenterCopyPrefix.Length + PresenterCopyHexLength
            || !stem[PresenterCopyPrefix.Length..].All(char.IsAsciiHexDigit))
        {
            throw new OperatorValidationException(PresenterNotNamedByDesktop);
        }

        var directory = MediaDirectoryOf(projectId);
        string? previous = null;
        var attached = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    if (project.Format != AnimatedCharacter)
                    {
                        throw new OperatorValidationException(NotAnimatedCharacter);
                    }

                    if (project.Status is not (ScriptReady or MediaReady or Assembled or Approved or AssemblyQueued or Assembling or Failed))
                    {
                        throw new OperatorValidationException(
                            $"A presenter picture is attached to a project whose script or media is ready, an assembled or approved one, one queued for assembly or assembling, or a failed one; this one is {project.Status}.");
                    }

                    if (!File.Exists(Path.Combine(directory, fileName)))
                    {
                        throw new OperatorValidationException(
                            $"'{fileName}' is not in the project's media folder; copy it there first.");
                    }

                    previous = UnversionedPresenterFile(project);
                    projects[index] = AfterSceneEdit(project, null) with
                    {
                        PresenterFile = fileName,
                        MouthBox = project.PresenterFile == fileName ? project.MouthBox : null,
                        PresenterPreviewFile = project.PresenterFile == fileName ? project.PresenterPreviewFile : null,
                        PresenterSource = SceneRules.Owner,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        if (previous is not null && previous != fileName && Path.GetFileName(previous) == previous)
        {
            try
            {
                File.Delete(Path.Combine(directory, previous));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Scout attached the new presenter picture but could not delete the one it replaced.");
            }
        }

        return ForDesktop(attached);
    }

    /// <summary>
    /// Store where the character's mouth is on the presenter picture of a project of an animated character whose script or
    /// media is ready, an assembled or approved one, one queued for assembly or a failed one: four fractions of the
    /// picture, each between 0 and 1, at least 0.02 wide and high and inside the picture. A box that changes the project's
    /// sends an assembled or approved project back to media ready without its rights and approval, like a scene edit.
    /// Scout's own state, so no grant is asked for and nothing is logged.
    /// </summary>
    /// <exception cref="OperatorValidationException">The box is missing or breaks a rule, the project is not an animated character, or it is on another status.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>.</exception>
    public async Task<OperatorProject> SetMouthBoxAsync(string projectId, MouthBox? mouthBox, CancellationToken ct)
    {
        if (mouthBox is not { } box || !WithinPicture(box))
        {
            throw new OperatorValidationException(MalformedMouthBox);
        }

        var marked = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = projects[index];
                    if (project.Format != AnimatedCharacter)
                    {
                        throw new OperatorValidationException(NotAnimatedCharacter);
                    }

                    if (project.Status is not (ScriptReady or MediaReady or Assembled or Approved or AssemblyQueued or Failed))
                    {
                        throw new OperatorValidationException(
                            $"A mouth box is set on a project whose script or media is ready, an assembled or approved one, one queued for assembly, or a failed one; this one is {project.Status}.");
                    }

                    if (project.MouthBox == box)
                    {
                        return project;
                    }

                    projects[index] = AfterSceneEdit(project, null) with { MouthBox = box, UpdatedAt = DateTimeOffset.UtcNow };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(marked);
    }

    /// <summary>
    /// Ask for another presenter for a project of an animated character, with no feedback and no prompt: exactly
    /// <see cref="RequestPresenterAsync"/> with both empty. The picture, mouth box, preview and versions the project has
    /// are kept until the worker's next take makes the new one. Scout's own state, so no grant is asked for and nothing
    /// is logged.
    /// </summary>
    /// <exception cref="OperatorValidationException">The project is not an animated character, or it is on a status a presenter cannot be asked for.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>.</exception>
    public Task<OperatorProject> RegeneratePresenterAsync(string projectId, CancellationToken ct) =>
        RequestPresenterAsync(projectId, string.Empty, string.Empty, ct);
    private const string MakingPresenter = "The project is making its presenter; wait for it to finish.";
    private const string PresenterFeedbackWord = "Presenter feedback cannot be first, another or prompt.";
    private const string FirstPresenter = "first";
    private const string PromptOnly = "prompt";
    private const string AnotherPresenter = "another";
    private const string BuiltInPrefix = "builtin:";
    private const int MaxPresenterFeedbackLength = 300;
    private const int MaxPresenterPromptLength = 1500;
    private const int MaxPresenterVersions = 10;
    private const int MaxPicturePromptLength = 700;
    private const string LongPresenterFeedback = "Presenter feedback is at most 300 characters.";
    private const string LongPresenterPrompt = "A presenter prompt is at most 1500 characters.";
    private const string MalformedBuiltIn = "A built-in presenter is named by its id of letters, digits, - and _.";
    private const string PicturePromptLength = "A picture prompt is 1 to 700 characters.";
    private const string NoGeneratedPicture = "This format has no generated picture.";
    private const string VersionNotMade = "A presenter version is a picture Scout made in the project's media folder, named presenter-.";
    private const string UnknownPresenterSource = "A presenter version is generated, builtin or owner.";
    private static readonly string[] PresenterSources = [SceneRules.Generated, "builtin", SceneRules.Owner];

    /// <summary>
    /// Ask the worker for a new presenter of a project of an animated character whose script or media is ready, one
    /// queued for production or assembly, an assembled or approved one, or a failed one with a script: the owner's
    /// <paramref name="feedback"/>, cleaned like a direction, at most 300 characters, or <c>builtin:</c> and the id of
    /// one of Scout's own presenters, and the owner's <paramref name="prompt"/>, cleaned the same way, at most 1500
    /// characters, used verbatim when given. The request is the feedback, <c>prompt</c> when only a prompt is given, or
    /// <c>another</c> when neither is; the worker's next take makes the presenter before any production. An assembled or
    /// approved project goes back to media ready without its rights and approval, like a scene edit. Scout's own state,
    /// so no grant is asked for and nothing is logged.
    /// </summary>
    /// <exception cref="OperatorValidationException">The feedback or prompt is too long, a built-in id is malformed, the project is not an animated character, or it is on another status.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>.</exception>
    public async Task<OperatorProject> RequestPresenterAsync(string projectId, string feedback, string prompt, CancellationToken ct)
    {
        var change = OwnerText(feedback);
        var owned = OwnerText(prompt);
        if (change.Length > MaxPresenterFeedbackLength)
        {
            throw new OperatorValidationException(LongPresenterFeedback);
        }

        if (owned.Length > MaxPresenterPromptLength)
        {
            throw new OperatorValidationException(LongPresenterPrompt);
        }

        if (change is FirstPresenter or AnotherPresenter or PromptOnly)
        {
            throw new OperatorValidationException(PresenterFeedbackWord);
        }

        if (change.StartsWith(BuiltInPrefix, StringComparison.Ordinal)
            && change[BuiltInPrefix.Length..] is var id
            && (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            throw new OperatorValidationException(MalformedBuiltIn);
        }

        var request = change.Length > 0 ? change : owned.Length > 0 ? PromptOnly : AnotherPresenter;
        var asked = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var index = IndexOf(projects, projectId);
                    var project = PresenterProject(projects[index]);
                    projects[index] = AfterSceneEdit(project, null) with
                    {
                        PresenterRequest = request,
                        PresenterRequestPrompt = owned,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(asked);
    }

    /// <summary>
    /// Make the presenter version named <paramref name="versionId"/> of a project of an animated character the current
    /// one again, its picture, mouth box, preview and source, on the statuses a presenter request is accepted on; a
    /// change sends an assembled or approved project back to media ready without its rights and approval, like a scene
    /// edit. Scout's own state, so no grant is asked for and nothing is logged.
    /// </summary>
    /// <exception cref="OperatorValidationException">The project is not an animated character, or is on another status.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>, or it has no version named <paramref name="versionId"/>.</exception>
    public async Task<OperatorProject> UsePresenterVersionAsync(string projectId, string versionId, CancellationToken ct)
    {
        var used = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var at = IndexOf(projects, projectId);
                    var project = PresenterProject(projects[at]);
                    var current = Current(project, project.PresenterVersions[VersionAt(project, versionId)]);
                    if (current == project)
                    {
                        return project;
                    }

                    projects[at] = AfterSceneEdit(current, null) with { UpdatedAt = DateTimeOffset.UtcNow };
                    return projects[at];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(used);
    }

    /// <summary>Scout's own presenters the worker reported in its latest readiness, none before it reported.</summary>
    public async Task<IReadOnlyList<BuiltInPresenter>> ListBuiltInPresentersAsync(CancellationToken ct) =>
        (await GetReadinessAsync(ct).ConfigureAwait(false)).BuiltInPresenters;

    /// <summary>
    /// Replace one unlocked scene's picture prompt with the owner's <paramref name="prompt"/>, cleaned like a direction,
    /// in a project of generated pictures or of an animated character; the scene and the project keep their status, so
    /// the next picture made for the scene follows it. Scout's own state, so no grant is asked for and nothing is logged.
    /// </summary>
    /// <exception cref="OperatorValidationException">The prompt is not 1 to 700 characters, the format has no generated picture, or the scene is locked.</exception>
    /// <exception cref="OperatorNotFoundException">The project or the scene does not exist.</exception>
    public async Task<OperatorProject> EditScenePicturePromptAsync(string projectId, string sceneId, string prompt, CancellationToken ct)
    {
        var owned = OwnerText(prompt);
        if (owned.Length is 0 or > MaxPicturePromptLength)
        {
            throw new OperatorValidationException(PicturePromptLength);
        }

        return await ChangeSceneAsync(
            projectId,
            sceneId,
            (project, scene) => GeneratedPictureScene(project, scene, $"Scene '{scene.Id}' is locked; unlock it before editing its picture prompt.") with { VisualPrompt = owned },
            _ => null,
            changesVideo: false,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Send one unlocked scene's picture back to be made again in a project of generated pictures or of an animated
    /// character, keeping its narration: the scene is planned without its error, its picture file and credit, its picture
    /// generated, and the project goes to production and its package back to assembled without its rights and approval,
    /// or to queued for assembly when it was queued or assembling, like a narration regeneration. Scout's own state, so
    /// no grant is asked for and nothing is logged.
    /// </summary>
    /// <exception cref="OperatorValidationException">The format has no generated picture, or the scene is locked.</exception>
    /// <exception cref="OperatorNotFoundException">The project or the scene does not exist.</exception>
    public Task<OperatorProject> RegenerateScenePictureAsync(string projectId, string sceneId, CancellationToken ct) =>
        ChangeSceneAsync(
            projectId,
            sceneId,
            (project, scene) => GeneratedPictureScene(project, scene, $"Scene '{scene.Id}' is locked; unlock it before regenerating it.") with
            {
                Status = SceneRules.Planned,
                Error = null,
                VisualSource = SceneRules.Generated,
                VisualFile = null,
                VisualCredit = null,
            },
            _ => ProductionQueued,
            changesVideo: true,
            ct);

    /// <summary>The owner's presenter request asked from the chat: <see cref="RequestPresenterAsync"/> under the grant, logged via chat.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> RequestPresenterFromChatAsync(string projectId, string feedback, string prompt, CancellationToken ct) =>
        ViaChatAsync(projectId, "asked for a new presenter", c => RequestPresenterAsync(projectId, feedback, prompt, c), ct);

    /// <summary>The owner's picture prompt edit asked from the chat: <see cref="EditScenePicturePromptAsync"/> under the grant, logged via chat.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> EditScenePicturePromptFromChatAsync(string projectId, string sceneId, string prompt, CancellationToken ct) =>
        ViaChatAsync(projectId, $"edited the picture prompt of scene {sceneId}", c => EditScenePicturePromptAsync(projectId, sceneId, prompt, c), ct);

    /// <summary>The owner's picture remake asked from the chat: <see cref="RegenerateScenePictureAsync"/> under the grant, logged via chat.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public Task<OperatorProject> RegenerateScenePictureFromChatAsync(string projectId, string sceneId, CancellationToken ct) =>
        ViaChatAsync(projectId, $"asked for the picture of scene {sceneId} again", c => RegenerateScenePictureAsync(projectId, sceneId, c), ct);

    private static Scene GeneratedPictureScene(OperatorProject project, Scene scene, string locked)
    {
        if (project.Format is OwnVideo or OwnPictures)
        {
            throw new OperatorValidationException(NoGeneratedPicture);
        }

        return scene.Locked ? throw new OperatorValidationException(locked) : scene;
    }

    private static OperatorProject PresenterProject(OperatorProject project)
    {
        if (project.Format != AnimatedCharacter)
        {
            throw new OperatorValidationException(NotAnimatedCharacter);
        }

        if (project.Status is not (ScriptReady or MediaReady or ProductionQueued or AssemblyQueued or Assembled or Approved)
            && !(project.Status == Failed && project.Script is not null))
        {
            throw new OperatorValidationException(
                $"A presenter can be asked for on a project whose script or media is ready, one queued for production or assembly, an assembled or approved one, or a failed one with a script; this one is {project.Status}.");
        }

        return project;
    }

    private static OperatorProject Current(OperatorProject project, PresenterVersion version) =>
        project with
        {
            PresenterFile = version.File,
            MouthBox = version.MouthBox,
            PresenterPreviewFile = version.PreviewFile,
            PresenterSource = version.Source,
        };

    private OperatorProject PresenterSaved(OperatorProject stored, OperatorProject sent, List<string> forgotten)
    {
        if (sent.PresenterRequest.Length > 0)
        {
            throw new OperatorTamperingException(
                $"The worker may not save the presenter of project '{stored.Id}' without clearing its presenter request.");
        }

        var added = sent.PresenterVersions.Count - stored.PresenterVersions.Count;
        if (added is < 0 or > 1 || !sent.PresenterVersions.Take(stored.PresenterVersions.Count).SequenceEqual(stored.PresenterVersions))
        {
            throw new OperatorTamperingException(
                $"The worker may add one presenter version to project '{stored.Id}' and change none it has.");
        }

        var restored = stored with
        {
            Status = stored.PresenterReturnStatus.Length > 0 ? stored.PresenterReturnStatus : ScriptReady,
            Error = sent.Error,
            PresenterRequest = string.Empty,
            PresenterRequestPrompt = string.Empty,
            PresenterReturnStatus = string.Empty,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (added == 0)
        {
            return restored;
        }

        var version = MadeVersion(stored, sent.PresenterVersions[^1]);
        var versions = stored.PresenterVersions.Append(version).ToList();
        var current = stored.PresenterRequest == FirstPresenter && stored.PresenterFile is not null
            ? version.File == stored.PresenterFile ? restored with { MouthBox = restored.MouthBox ?? version.MouthBox } : restored
            : Current(restored, version);
        if (versions.Count > MaxPresenterVersions)
        {
            var dropped = versions[Math.Max(0, versions.FindIndex(v => v.File != current.PresenterFile))];
            versions.Remove(dropped);
            forgotten.AddRange(new[] { dropped.File, dropped.PreviewFile }.OfType<string>().Where(file =>
                file != current.PresenterFile && file != current.PresenterPreviewFile
                && !versions.Any(v => v.File == file || v.PreviewFile == file)));
        }

        return current with { PresenterVersions = versions };
    }

    private PresenterVersion MadeVersion(OperatorProject stored, PresenterVersion version)
    {
        var directory = MediaDirectoryOf(stored.Id);
        SceneRules.ValidateFileName(version.File, SceneRules.Visual);
        if (version.PreviewFile is not null)
        {
            SceneRules.ValidateFileName(version.PreviewFile, SceneRules.Video);
        }

        var ownersPicture = version.Source == SceneRules.Owner && version.File == stored.PresenterFile;
        if (new[] { version.File, version.PreviewFile }.OfType<string>().Any(file =>
                !(file.StartsWith(WorkerPresenterPrefix, StringComparison.Ordinal) || (ownersPicture && file == version.File))
                || !File.Exists(Path.Combine(directory, file))))
        {
            throw new OperatorValidationException(VersionNotMade);
        }

        if (version.MouthBox is { } box && !WithinPicture(box))
        {
            throw new OperatorValidationException(MalformedMouthBox);
        }

        return PresenterSources.Contains(version.Source, StringComparer.Ordinal)
            ? version with { MadeAt = DateTimeOffset.UtcNow }
            : throw new OperatorValidationException(UnknownPresenterSource);
    }

    private void DeleteForgotten(string projectId, IReadOnlyList<string> files)
    {
        foreach (var file in files.Where(f => Path.GetFileName(f) == f))
        {
            try
            {
                File.Delete(Path.Combine(MediaDirectoryOf(projectId), file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Scout dropped an old presenter version but could not delete its file.");
            }
        }
    }

    /// <summary>
    /// Remove the presenter version named <paramref name="versionId"/> of a project, one the owner made by mistake:
    /// any version but the last one naming the current picture and preview, on any status but presenting. The
    /// project keeps its presenter, status and approval, since the video does not change, and once the store holds the
    /// change the version's picture and preview are deleted from the media directory unless the current presenter or
    /// another version still names them, a file that cannot be deleted logged and left. Scout's own state, so no grant
    /// is asked for and nothing is logged.
    /// </summary>
    /// <exception cref="OperatorValidationException">The project is making its presenter, or the version is the current one.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="projectId"/>, or it has no version named <paramref name="versionId"/>.</exception>
    public async Task<OperatorProject> RemovePresenterVersionAsync(string projectId, string versionId, CancellationToken ct)
    {
        var forgotten = new List<string>();
        var removed = await _store.UpdateProjectsAsync(
                projects =>
                {
                    var at = IndexOf(projects, projectId);
                    var project = projects[at];
                    if (project.Status == Presenting)
                    {
                        throw new OperatorValidationException(MakingPresenter);
                    }

                    var index = VersionAt(project, versionId);
                    var dropped = project.PresenterVersions[index];
                    var versions = project.PresenterVersions.Where((_, i) => i != index).ToList();
                    if (dropped.File == project.PresenterFile && dropped.PreviewFile == project.PresenterPreviewFile
                        && !versions.Any(v => v.File == dropped.File && v.PreviewFile == dropped.PreviewFile))
                    {
                        throw new OperatorValidationException(CurrentPresenterKept);
                    }

                    forgotten.AddRange(new[] { dropped.File, dropped.PreviewFile }.OfType<string>().Where(file =>
                        file != project.PresenterFile && file != project.PresenterPreviewFile
                        && !versions.Any(v => v.File == file || v.PreviewFile == file)));
                    projects[at] = project with { PresenterVersions = versions, UpdatedAt = DateTimeOffset.UtcNow };
                    return projects[at];
                },
                ct)
            .ConfigureAwait(false);
        DeleteForgotten(projectId, forgotten);
        return ForDesktop(removed);
    }

    private static int VersionAt(OperatorProject project, string versionId) =>
        project.PresenterVersions.ToList().FindIndex(v => v.Id == versionId) is var index and >= 0
            ? index
            : throw new OperatorNotFoundException(VersionGone);

    private const string CurrentPresenterKept = "The current presenter cannot be removed; choose another version first.";
    private const string VersionGone = "That presenter version is no longer there.";
}
