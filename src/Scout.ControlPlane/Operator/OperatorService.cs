using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;

namespace Scout.ControlPlane.Operator;

/// <summary>
/// The single writer of the channel operator's state. The desktop edits the mandate, starts and
/// produces projects under the owner's live write grant of the YouTube feature, decides claims and
/// edits scenes; the worker reads the mandate, reports its readiness, and reads projects, takes
/// queued ones and saves its drafts under the same grant, each save and each take recorded in the
/// write log. The worker can never mark a claim supported or waived, change an owner's decision,
/// change a project's topic or origin, or reorder, lock, change a locked scene or replace the
/// owner's media. The desktop never receives a project's origin reference.
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
    private const string ReadinessNotSaved = "Scout could not save the worker's readiness report in its data folder.";
    private const string DataFolderScope = "Scout's data folder";
    private const int DefaultListLimit = 50;
    private const int MaxListLimit = 200;
    private const int MaxPillars = 5;
    private const int MaxPillarLength = 80;
    private const int MinTopicLength = 3;
    private const int MaxTopicLength = 200;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    private static readonly string[] WorkerStatuses = [Researching, Scripting, ScriptReady, Producing, MediaReady, Failed];
    private static readonly string[] WorkerClaimStatuses = ["pending", "unsupported"];
    private static readonly string[] OwnerClaimStatuses = ["supported", "unsupported", "waived"];

    private readonly JsonFileOperatorStore _store;
    private readonly WriteGateService _gate;
    private readonly IWriteLog _writes;

    /// <summary>Build the service over its store, the write gate that holds the grant check, and the write log.</summary>
    public OperatorService(JsonFileOperatorStore store, WriteGateService gate, IWriteLog writes)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
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

    /// <summary>The newest projects first; a <paramref name="limit"/> of 0 or less means 50, and at most 200 are returned.</summary>
    public async Task<IReadOnlyList<OperatorProject>> ListProjectsAsync(int limit, CancellationToken ct)
    {
        var take = limit <= 0 ? DefaultListLimit : Math.Min(limit, MaxListLimit);
        var projects = await _store.ReadProjectsAsync(ct).ConfigureAwait(false);
        return Enumerable.Reverse(projects).Take(take).Select(ForDesktop).ToList();
    }

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
    /// A change only the owner or the desktop may make, any save on a project the owner queued for production again,
    /// or a save of producing, media ready or any scene change on a project that is no longer producing.
    /// </exception>
    /// <exception cref="OperatorNotFoundException">No project has the id.</exception>
    public async Task<OperatorProject> SaveProjectFromWorkerAsync(
        OperatorProject project,
        string? originReference,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        await RequireGrantAsync(project.Id, ct).ConfigureAwait(false);
        if (!WorkerStatuses.Contains(project.Status, StringComparer.Ordinal))
        {
            throw new OperatorValidationException(
                $"Status '{project.Status}' is not one the worker may set: {string.Join(", ", WorkerStatuses)}.");
        }

        ValidateClaims(project.Claims);
        SceneRules.Validate(project.Scenes);

        if (string.IsNullOrEmpty(project.Id))
        {
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
            await LogSaveAsync(created, ct).ConfigureAwait(false);
            return created;
        }

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

                    var claims = MergeClaims(stored.Claims, project.Claims);
                    var scenes = SceneRules.MergeFromWorker(stored.Id, stored.Scenes, project.Scenes);
                    if (stored.Status == ProductionQueued
                        || (stored.Status != Producing
                            && (project.Status is Producing or MediaReady || !scenes.SequenceEqual(stored.Scenes))))
                    {
                        throw new OperatorTamperingException(NoLongerProducing);
                    }

                    projects[index] = stored with
                    {
                        Status = project.Status,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Candidates = project.Candidates,
                        Script = project.Script,
                        Claims = claims,
                        Error = project.Error,
                        Scenes = scenes,
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        await LogSaveAsync(saved, ct).ConfigureAwait(false);
        return saved;
    }

    /// <summary>One project for the worker under the owner's write grant, with the owner's claim decisions.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorNotFoundException">No project has <paramref name="id"/>.</exception>
    public async Task<OperatorProject> GetProjectForWorkerAsync(string id, CancellationToken ct)
    {
        await RequireGrantAsync(id, ct).ConfigureAwait(false);
        return await GetProjectAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>The newest projects first for the worker under the owner's write grant, with the desktop's limit rules.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public async Task<IReadOnlyList<OperatorProject>> ListProjectsForWorkerAsync(int limit, CancellationToken ct)
    {
        await RequireGrantAsync("operator projects", ct).ConfigureAwait(false);
        return await ListProjectsAsync(limit, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Under the owner's write grant take, in one step so two callers never take the same project,
    /// the oldest queued project and move it to researching; else the oldest project queued for
    /// production and move it to producing; else the oldest project left in researching, scripting
    /// or producing with no save for 15 minutes, back in researching or producing. The take is
    /// recorded in the write log. Null when none waits, and then the projects file is not written.
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

                    projects[index] = projects[index] with { Status = status, UpdatedAt = now };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        if (taken is not null)
        {
            await LogSaveAsync(taken, ct).ConfigureAwait(false);
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

    /// <summary>Put the project's scenes in the owner's order; <paramref name="sceneIds"/> names every scene once, and the first is order 1.</summary>
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
                    projects[index] = project with { Scenes = scenes, UpdatedAt = DateTimeOffset.UtcNow };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(reordered);
    }

    /// <summary>Lock or unlock one scene; a locked scene is never changed by the worker or regenerated.</summary>
    /// <exception cref="OperatorNotFoundException">The project or the scene does not exist.</exception>
    public Task<OperatorProject> SetSceneLockedAsync(string projectId, string sceneId, bool locked, CancellationToken ct) =>
        ChangeSceneAsync(projectId, sceneId, scene => scene with { Locked = locked }, projectStatus: null, ct);

    /// <summary>
    /// Send one unlocked scene back to planned and the project to production: the scene keeps the
    /// owner's media and loses its generated files and error.
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
            ct);

    /// <summary>
    /// Use the owner's own audio or visual for one scene. The file must already be in the project's
    /// media directory. The scene is ready once it has a visual file, or a text card production chose,
    /// and an audio file, or no audio production chose; a scene production has not made yet counts only
    /// files. The owner's audio leaves the duration unknown, 0.
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

        var stale = FirstIndex(projects, p => p.Status is Researching or Scripting or Producing && now - p.UpdatedAt > StaleAfter);
        return stale < 0 ? (-1, string.Empty) : (stale, projects[stale].Status == Producing ? Producing : Researching);
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

    private static OperatorProject ForDesktop(OperatorProject project) => project with { OriginReference = null };

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

    private static List<Claim> MergeClaims(IReadOnlyList<Claim> stored, IReadOnlyList<Claim> incoming)
    {
        foreach (var decided in stored.Where(c => c.DecidedAt is not null))
        {
            var sent = incoming.FirstOrDefault(c => string.Equals(c.Id, decided.Id, StringComparison.Ordinal));
            if (sent is null
                || !string.Equals(sent.Text, decided.Text, StringComparison.Ordinal)
                || !string.Equals(sent.Status, decided.Status, StringComparison.Ordinal)
                || !string.Equals(sent.Note, decided.Note, StringComparison.Ordinal)
                || !string.Equals(sent.Risk, decided.Risk, StringComparison.Ordinal)
                || !sent.SourceUrls.SequenceEqual(decided.SourceUrls, StringComparer.Ordinal))
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
                    projects[index] = project with
                    {
                        Scenes = scenes,
                        Status = projectStatus ?? project.Status,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return projects[index];
                },
                ct)
            .ConfigureAwait(false);
        return ForDesktop(changed);
    }

    private Task LogSaveAsync(OperatorProject project, CancellationToken ct) =>
        _writes.AppendAsync(
            new WriteLogEntry(
                DateTimeOffset.UtcNow,
                FeatureCatalog.YouTubeId,
                DataFolderScope,
                project.Id,
                $"saved {project.Status}",
                ApprovedBy: null,
                ActionId: null),
            ct);

    private async Task RequireGrantAsync(string target, CancellationToken ct)
    {
        var decision = await _gate.RequestAsync(
                FeatureCatalog.YouTubeId,
                DataFolderScope,
                target,
                "Save the channel operator's state",
                ct)
            .ConfigureAwait(false);
        if (decision.Kind != WriteDecisionKind.Allowed)
        {
            throw new OperatorWriteRefusedException(decision.Reason);
        }
    }
}
