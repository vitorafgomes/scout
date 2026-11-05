using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;

namespace Scout.Operator;

/// <summary>
/// Runs one project from research to a script ready for the owner's review: researching, then scripting, then
/// <c>script_ready</c>, or <c>failed</c> with a sentence saying why, saving through the control plane after
/// each stage. Every save starts from the project as the control plane holds it, so a claim the owner decided
/// goes back exactly as the owner left it. When the project came from a chat, it posts there that research
/// started and that the script is ready, or the failure, even when the failure cannot be saved; it posts
/// nothing else. A failure that cannot be saved is thrown as an <see cref="InvalidOperationException"/>
/// carrying the original reason, so a later save records the real cause, never the save's own failure. A topic
/// the mandate blocks fails the project before any search. A project taken as <c>producing</c> runs the production
/// stage instead: scenes planned from the script when it has none, then narration and a picture for every unlocked
/// scene that is not ready, or whose narration or picture is not done given the speech and image services
/// available now, saved after each scene, ending <c>media_ready</c>.
/// Production reads the project again before each scene and each save; once the project is no longer
/// <c>producing</c>, because the owner regenerated a scene or produced it again, read so or refused so by the control
/// plane, the run stops without saving. A project taken as <c>assembling</c> runs the assembly stage: the video, its
/// Shorts, its thumbnail and its checks rendered in the media directory and saved as the package, ending
/// <c>assembled</c> with the segments of that video kept and older ones removed, or <c>failed</c> with the reason and
/// the partial files removed; it stops the same way once the project is no longer <c>assembling</c>. A project taken as
/// <c>transcribing</c> has the owner's recording transcribed on this machine into a script of timed sections, saved at most
/// once a minute, ending <c>script_ready</c>, or <c>failed</c> with no partial transcript, and it stops the same way once
/// the project is no longer <c>transcribing</c>; production of such a project plans its scenes as clips of the recording
/// and makes no narration or picture, and once it has scenes names their chapters again under the owner's direction,
/// leaving the clips as they are. Production of an animated character makes the presenter picture once, when the project
/// has none, then only the narration, every scene shown by the presenter, a picture that could not be made being the
/// project's note.
/// </summary>
internal sealed class OperatorPipeline(
    IOperatorControlPlane controlPlane,
    TopicResearcher researcher,
    ScriptWriter writer,
    ScenePlanner planner,
    NarrationGenerator narration,
    VisualGenerator visuals,
    AssemblyStage assembly,
    TranscriptionStage transcription,
    IOriginNotifier notifier,
    ILogger<OperatorPipeline> logger)
{
    internal const string Researching = "researching";
    internal const string Scripting = "scripting";
    internal const string ScriptReady = "script_ready";
    internal const string Failed = "failed";
    internal const string Pending = "pending";
    internal const string Producing = "producing";
    internal const string MediaReady = "media_ready";
    internal const string Planned = "planned";
    internal const string Ready = "ready";
    internal const string Generated = "generated";
    internal const string Owner = "owner";
    internal const string NoAudio = "none";
    internal const string TextCard = "text_card";
    internal const string Stock = "stock";
    internal const string OwnPictures = "own_pictures";
    internal const string NoScript = "The project has no script to produce; write the script first.";
    internal const string NoLongerProducing = "The project is no longer producing.";
    internal const string Assembling = "assembling";
    internal const string NoLongerAssembling = "The project is no longer assembling.";
    internal const string Transcribing = "transcribing";
    internal const string NoLongerTranscribing = "The project is no longer transcribing; the worker's transcript was not saved.";
    internal const string OwnVideo = "own_video";
    internal const string Recording = "recording";
    internal const string AnimatedCharacter = "animated_character";
    internal const string Presenter = "presenter";

    internal const string NoPresenterPicture =
        "The project has no presenter picture; attach one in Scout Desktop, Production, or connect an AI Source with pictures.";

    internal const string PresenterStyle =
        "flat 2D cartoon illustration, front-facing, looking at the viewer, mouth closed, neutral expression, plain background, head and shoulders";

    internal static string ResearchingNotice(string topic) => $"Researching {topic}...";

    internal static string ReadyNotice(string topic) =>
        $"Script ready for {topic}: open Scout Desktop, Production, to review it.";

    internal static string ProducingNotice(string topic) => $"Producing narration and visuals for {topic}...";

    internal static string MediaReadyNotice(string topic) =>
        $"Media ready for {topic}: review the scenes in Scout Desktop, Production.";

    internal static string AssemblingNotice(string topic) => $"Assembling {topic}...";

    internal static string AssembledNotice(string topic) =>
        $"Video ready to review for {topic}: open Scout Desktop, YouTube operator.";

    internal static string FailedNotice(string topic, string error) =>
        $"The video project on {topic} stopped: {error}";

    internal static string Reason(Exception ex) =>
        ex is InvalidOperationException ? ex.Message : $"Scout hit an unexpected error ({ex.GetType().Name}).";

    public async Task RunAsync(QueuedProject queued, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queued);
        var id = queued.Project.Id;
        var topic = queued.Project.Topic;
        var status = queued.Project.Status;
        var producing = status == Producing;
        var assembling = status == Assembling;
        var transcribing = status == Transcribing;
        await NotifyAsync(
            queued.OriginReference,
            assembling ? AssemblingNotice(topic) : producing ? ProducingNotice(topic) : transcribing ? null : ResearchingNotice(topic),
            ct).ConfigureAwait(false);

        var finished = true;
        try
        {
            if (assembling)
            {
                finished = await AssembleAsync(id, ct).ConfigureAwait(false);
            }
            else if (producing)
            {
                finished = await ProduceAsync(id, ct).ConfigureAwait(false);
            }
            else if (transcribing)
            {
                finished = await TranscribeAsync(id, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteScriptAsync(queued.Project, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reason = Reason(ex);
            logger.LogWarning("Video project {ProjectId} failed ({ExceptionType}).", id, ex.GetType().Name);
            var left = false;
            try
            {
                if (producing || assembling || transcribing)
                {
                    left = await SaveStageAsync(id, status, current => Failing(current, reason), ct).ConfigureAwait(false) is null;
                }
                else
                {
                    await SaveFailedAsync(id, reason, ct).ConfigureAwait(false);
                }
            }
            catch (Exception saveFailure) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException(reason, saveFailure);
            }
            finally
            {
                if (!left)
                {
                    await NotifyAsync(queued.OriginReference, FailedNotice(topic, reason), ct).ConfigureAwait(false);
                }
            }

            return;
        }

        if (!finished)
        {
            logger.LogInformation("Video project {ProjectId} left {Status} while it ran; the run stopped.", id, status);
            return;
        }

        await NotifyAsync(
            queued.OriginReference,
            assembling ? AssembledNotice(topic) : producing ? MediaReadyNotice(topic) : ReadyNotice(topic),
            ct).ConfigureAwait(false);
    }

    private async Task WriteScriptAsync(OperatorProjectState project, CancellationToken ct)
    {
        var id = project.Id;
        var topic = project.Topic;
        var mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
        if (TopicResearcher.BlockedTopic(mandate, topic) is { } blocked)
        {
            throw new InvalidOperationException(TopicResearcher.Blocked(blocked));
        }

        var research = await researcher.ResearchAsync(topic, mandate, ct).ConfigureAwait(false);
        await SaveAsync(id, current => current with { Status = Scripting, Candidates = research.Candidates, Error = null }, ct)
            .ConfigureAwait(false);
        var draft = await writer.WriteAsync(topic, mandate, research, project.Format, project.Direction, ct).ConfigureAwait(false);
        await SaveAsync(
            id,
            current => current with
            {
                Status = ScriptReady,
                Script = draft.Script,
                Claims = WithOwnerDecisions(current.Claims, draft.Claims),
                Error = null,
            },
            ct).ConfigureAwait(false);
    }

    private async Task<bool> ProduceAsync(string id, CancellationToken ct)
    {
        var directory = await controlPlane.GetMediaDirectoryAsync(id, ct).ConfigureAwait(false);
        OperatorProjectState? project = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
        if (project.Status != Producing)
        {
            return false;
        }

        var mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
        var ownVideo = project.Format == OwnVideo;
        if (project.Scenes.Count == 0)
        {
            var script = project.Script ?? throw new InvalidOperationException(NoScript);
            if (ownVideo)
            {
                var plan = await planner.PlanRecordingAsync(script, project.RecordingMs, mandate, project.Direction, ct).ConfigureAwait(false);
                project = await SaveProductionAsync(
                    id, current => current.Scenes.Count == 0 ? current with { Scenes = plan.Scenes, Script = plan.Script } : current, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var planned = await planner.PlanAsync(script, mandate, project.Direction, ct).ConfigureAwait(false);
                project = await SaveProductionAsync(id, current => current.Scenes.Count == 0 ? current with { Scenes = planned } : current, ct)
                    .ConfigureAwait(false);
            }

            if (project is null)
            {
                return false;
            }
        }
        else if (ownVideo)
        {
            var named = await planner.NameChaptersAsync([.. project.Scenes.OrderBy(s => s.Order)], mandate, project.Direction, ct)
                .ConfigureAwait(false);
            if (await SaveProductionAsync(id, current => current with { Script = named }, ct).ConfigureAwait(false) is null)
            {
                return false;
            }
        }

        if (ownVideo)
        {
            return await SaveProductionAsync(id, current => current with { Status = MediaReady, Error = null }, ct)
                .ConfigureAwait(false) is not null;
        }

        var animated = project.Format == AnimatedCharacter;
        string? presenterNote = null;
        if (animated)
        {
            (var running, presenterNote) = await PresenterPictureAsync(id, directory, ct).ConfigureAwait(false);
            if (!running)
            {
                return false;
            }
        }

        var speechNow = await narration.SpeechAvailableAsync(ct).ConfigureAwait(false);
        var ownPictures = project.Format == OwnPictures;
        var imagesNow = visuals.ImagesAvailable && !ownPictures;
        foreach (var sceneId in project.Scenes.OrderBy(s => s.Order).Select(s => s.Id).ToList())
        {
            var current = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
            if (current.Status != Producing)
            {
                return false;
            }

            var ordered = current.Scenes.OrderBy(s => s.Order).ToList();
            var position = ordered.FindIndex(s => s.Id == sceneId) + 1;
            if (position == 0)
            {
                continue;
            }

            var scene = ordered[position - 1];
            if (scene.Locked || !NeedsMedia(scene, directory, speechNow, imagesNow, animated))
            {
                continue;
            }

            NarrationResult? audio;
            VisualResult? visual;
            try
            {
                audio = scene.AudioSource == Owner || (scene.Status == Ready && AudioDone(scene, directory, speechNow)) ? null : await narration.GenerateAsync(scene, current.NarrationVoice, directory, ct).ConfigureAwait(false);
                visual = animated
                    ? scene.VisualSource == Presenter ? null : new VisualResult(Presenter, null, null)
                    : PictureKept(scene, directory) || (scene.Status == Ready && scene.VisualSource == TextCard && !imagesNow)
                    ? null
                    : ownPictures
                        ? new VisualResult(TextCard, null, VisualGenerator.NoOwnPicture)
                        : await visuals.GenerateAsync(scene, position, directory, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var reason = Reason(ex);
                try
                {
                    if (await SaveProductionAsync(id, p => WithScene(p, sceneId, s => s with { Status = Failed, Error = reason }), ct)
                        .ConfigureAwait(false) is null)
                    {
                        return false;
                    }
                }
                catch (Exception saveFailure) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Scene {SceneId} of video project {ProjectId} could not be saved as failed ({ExceptionType}).",
                        sceneId, id, saveFailure.GetType().Name);
                }

                throw new InvalidOperationException(reason, ex);
            }

            if (await SaveProductionAsync(id, p => WithScene(p, sceneId, s => WithMedia(s, audio, visual)), ct)
                .ConfigureAwait(false) is null)
            {
                return false;
            }
        }

        return await SaveProductionAsync(id, current => current with { Status = MediaReady, Error = presenterNote }, ct)
            .ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Make the animated character's picture once, when the project has none: one request to the image service from the
    /// owner's direction, the presenter description, or the topic without one, <see cref="PresenterStyle"/> and
    /// <see cref="VisualGenerator.NoRealPerson"/>, counted in
    /// the day's pictures and saved as <c>presenter-&lt;12 hex&gt;.png</c> on the project unless the owner attached one
    /// meanwhile. A picture that could not be made is <see cref="NoPresenterPicture"/>, the project's note, never a failure;
    /// not running when the project is no longer producing.
    /// </summary>
    private async Task<(bool Running, string? Note)> PresenterPictureAsync(string id, string directory, CancellationToken ct)
    {
        var project = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
        if (project.Status != Producing)
        {
            return (false, null);
        }

        if (!string.IsNullOrEmpty(project.PresenterFile))
        {
            return (true, null);
        }

        var file = $"presenter-{RandomNumberGenerator.GetHexString(12, lowercase: true)}.png";
        bool made;
        try
        {
            made = await visuals.PictureAsync(PresenterPrompt(project), directory, file, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            made = false;
        }

        if (!made)
        {
            return (true, NoPresenterPicture);
        }

        var saved = await SaveProductionAsync(
            id, current => string.IsNullOrEmpty(current.PresenterFile) ? current with { PresenterFile = file } : current, ct)
            .ConfigureAwait(false);
        if (saved?.PresenterFile != file)
        {
            MediaFiles.Delete(directory, file);
        }

        return (saved is not null, null);
    }

    internal static string PresenterPrompt(OperatorProjectState project)
        => string.Join(
            ". ",
            new[] { project.Direction, project.Presenter.Length > 0 ? project.Presenter : project.Topic, $"{PresenterStyle}, {VisualGenerator.NoRealPerson}" }
                .Select(part => part.Trim().TrimEnd('.'))
                .Where(part => part.Length > 0)) + ".";

    /// <summary>
    /// Save a change to the project as the control plane holds it now, read immediately before; null, and nothing saved,
    /// when the project is no longer producing, read so or refused so by the control plane.
    /// </summary>
    private Task<OperatorProjectState?> SaveProductionAsync(
        string id, Func<OperatorProjectState, OperatorProjectState> change, CancellationToken ct)
        => SaveStageAsync(id, Producing, change, ct);

    /// <summary>
    /// Save a change to the project as the control plane holds it now, read immediately before; null, and nothing saved,
    /// when the project is no longer in <paramref name="stage"/>, read so or refused so by the control plane.
    /// </summary>
    private async Task<OperatorProjectState?> SaveStageAsync(
        string id, string stage, Func<OperatorProjectState, OperatorProjectState> change, CancellationToken ct)
    {
        var current = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
        if (current.Status != stage)
        {
            return null;
        }

        try
        {
            return await controlPlane.SaveProjectAsync(change(current), null, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message == stage switch
        {
            Assembling => NoLongerAssembling,
            Transcribing => NoLongerTranscribing,
            _ => NoLongerProducing,
        })
        {
            return null;
        }
    }

    private async Task<bool> TranscribeAsync(string id, CancellationToken ct)
    {
        var directory = await controlPlane.GetMediaDirectoryAsync(id, ct).ConfigureAwait(false);
        var project = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
        if (project.Status != Transcribing)
        {
            return false;
        }

        var transcript = await transcription.TranscribeAsync(
            project,
            directory,
            async (recordingMs, soFar) => await SaveStageAsync(
                id, Transcribing, current => current with { RecordingMs = recordingMs, Script = soFar, Error = null }, ct)
                .ConfigureAwait(false) is not null,
            ct).ConfigureAwait(false);
        return transcript is not null
            && await SaveStageAsync(id, Transcribing, current => current with { Status = ScriptReady, Script = transcript, Error = null }, ct)
                .ConfigureAwait(false) is not null;
    }

    private async Task<bool> AssembleAsync(string id, CancellationToken ct)
    {
        var tools = await assembly.ToolsAsync(ct).ConfigureAwait(false);
        var directory = await controlPlane.GetMediaDirectoryAsync(id, ct).ConfigureAwait(false);
        var project = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
        if (project.Status != Assembling)
        {
            return false;
        }

        var mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
        var render = await assembly.RenderAsync(tools, project, directory, ct).ConfigureAwait(false);
        OperatorProjectState? saved;
        try
        {
            saved = await SaveStageAsync(
                id,
                Assembling,
                current => current with { Status = AssemblyStage.Assembled, Error = null, Package = AssemblyStage.Package(current, render, mandate) },
                ct).ConfigureAwait(false);
        }
        catch
        {
            AssemblyStage.Discard(directory);
            throw;
        }

        if (saved is not null)
        {
            AssemblyStage.Prune(directory, render);
        }

        return saved is not null;
    }

    private static bool NeedsMedia(OperatorScene scene, string directory, bool speechNow, bool imagesNow, bool animated)
        => scene.Status != Ready
            || !AudioDone(scene, directory, speechNow)
            || !(animated ? scene.VisualSource == Presenter : VisualDone(scene, directory, imagesNow));

    private static bool AudioDone(OperatorScene scene, string directory, bool speechNow)
        => scene.AudioSource == Owner
            || (scene.AudioSource == Generated && scene.AudioFile is not null && MediaFiles.Exists(directory, scene.AudioFile))
            || (scene.AudioSource == NoAudio && !speechNow);

    private static bool VisualDone(OperatorScene scene, string directory, bool imagesNow)
        => PictureKept(scene, directory) || (scene.VisualSource == TextCard && !imagesNow);

    private static bool PictureKept(OperatorScene scene, string directory)
        => scene.VisualSource == Owner
            || (scene.VisualSource is Generated or Stock && scene.VisualFile is not null && MediaFiles.Exists(directory, scene.VisualFile));

    private static OperatorScene WithMedia(OperatorScene current, NarrationResult? audio, VisualResult? visual)
    {
        var next = current with
        {
            Status = Ready,
            Error = string.Join(" ", new[] { audio?.Note, visual?.Note }.Where(n => n is not null)) is { Length: > 0 } notes ? notes : null,
        };
        if (audio is not null && current.AudioSource != Owner)
        {
            next = next with { AudioSource = audio.AudioSource, AudioFile = audio.AudioFile, DurationMs = audio.DurationMs };
        }

        if (visual is not null && current.VisualSource != Owner)
        {
            next = next with { VisualSource = visual.VisualSource, VisualFile = visual.VisualFile, VisualCredit = visual.Credit };
        }

        return next;
    }

    private static OperatorProjectState WithScene(OperatorProjectState current, string sceneId, Func<OperatorScene, OperatorScene> change)
        => current with { Scenes = [.. current.Scenes.Select(s => s.Id == sceneId && !s.Locked ? change(s) : s)] };

    /// <summary>
    /// Save the project as failed with <paramref name="error"/>, starting from the control plane's copy; an assembling
    /// project's package fails with it, keeping the owner's fields, and a transcribing project keeps no partial transcript.
    /// </summary>
    public Task SaveFailedAsync(string id, string error, CancellationToken ct)
        => SaveAsync(id, current => Failing(current, error), ct);

    private static OperatorProjectState Failing(OperatorProjectState current, string reason) => current.Status switch
    {
        Assembling => AssemblyStage.Failed(current, reason),
        Transcribing => current with { Status = Failed, Error = reason, Script = null },
        _ => current with { Status = Failed, Error = reason },
    };

    private async Task SaveAsync(string id, Func<OperatorProjectState, OperatorProjectState> change, CancellationToken ct)
    {
        var current = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
        await controlPlane.SaveProjectAsync(change(current), null, ct).ConfigureAwait(false);
    }

    private static List<OperatorClaim> WithOwnerDecisions(IReadOnlyList<OperatorClaim> stored, IReadOnlyList<OperatorClaim> drafted)
    {
        var decided = stored.Where(c => c.Status != Pending).ToList();
        var usedIds = decided.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var next = 0;
        var fresh = drafted
            .Where(c => !decided.Any(d => string.Equals(d.Text, c.Text, StringComparison.Ordinal)))
            .Select(c =>
            {
                string claimId;
                do
                {
                    claimId = $"c{++next}";
                }
                while (usedIds.Contains(claimId));

                return c with { Id = claimId };
            });
        return [.. decided, .. fresh];
    }

    private async Task NotifyAsync(string? originReference, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(originReference) || text is null)
        {
            return;
        }

        try
        {
            if (!await notifier.PostAsync(originReference, text, ct).ConfigureAwait(false))
            {
                logger.LogInformation("The progress notice could not be posted to the originating conversation.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Posting the progress notice failed ({ExceptionType}).", ex.GetType().Name);
        }
    }
}
