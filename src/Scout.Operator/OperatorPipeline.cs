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
/// scene that is not ready or whose generated files are gone, saved after each scene, ending <c>media_ready</c>.
/// Production reads the project again before each scene and each save; once the project is no longer
/// <c>producing</c>, because the owner regenerated a scene or produced it again, read so or refused so by the control
/// plane, the run stops without saving.
/// </summary>
internal sealed class OperatorPipeline(
    IOperatorControlPlane controlPlane,
    TopicResearcher researcher,
    ScriptWriter writer,
    ScenePlanner planner,
    NarrationGenerator narration,
    VisualGenerator visuals,
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
    internal const string NoScript = "The project has no script to produce; write the script first.";
    internal const string NoLongerProducing = "The project is no longer producing.";

    internal static string ResearchingNotice(string topic) => $"Researching {topic}...";

    internal static string ReadyNotice(string topic) =>
        $"Script ready for {topic}: open Scout Desktop, Production, to review it.";

    internal static string ProducingNotice(string topic) => $"Producing narration and visuals for {topic}...";

    internal static string MediaReadyNotice(string topic) =>
        $"Media ready for {topic}: review the scenes in Scout Desktop, Production.";

    internal static string FailedNotice(string topic, string error) =>
        $"The video project on {topic} stopped: {error}";

    internal static string Reason(Exception ex) =>
        ex is InvalidOperationException ? ex.Message : $"Scout hit an unexpected error ({ex.GetType().Name}).";

    public async Task RunAsync(QueuedProject queued, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queued);
        var id = queued.Project.Id;
        var topic = queued.Project.Topic;
        var producing = queued.Project.Status == Producing;
        await NotifyAsync(queued.OriginReference, producing ? ProducingNotice(topic) : ResearchingNotice(topic), ct)
            .ConfigureAwait(false);

        var finished = true;
        try
        {
            if (producing)
            {
                finished = await ProduceAsync(id, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteScriptAsync(id, topic, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reason = Reason(ex);
            logger.LogWarning("Video project {ProjectId} failed ({ExceptionType}).", id, ex.GetType().Name);
            var left = false;
            try
            {
                if (producing)
                {
                    left = await SaveProductionAsync(id, current => current with { Status = Failed, Error = reason }, ct)
                        .ConfigureAwait(false) is null;
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
            logger.LogInformation("Video project {ProjectId} left production while it ran; the run stopped.", id);
            return;
        }

        await NotifyAsync(queued.OriginReference, producing ? MediaReadyNotice(topic) : ReadyNotice(topic), ct)
            .ConfigureAwait(false);
    }

    private async Task WriteScriptAsync(string id, string topic, CancellationToken ct)
    {
        var mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
        if (TopicResearcher.BlockedTopic(mandate, topic) is { } blocked)
        {
            throw new InvalidOperationException(TopicResearcher.Blocked(blocked));
        }

        var research = await researcher.ResearchAsync(topic, mandate, ct).ConfigureAwait(false);
        await SaveAsync(id, current => current with { Status = Scripting, Candidates = research.Candidates, Error = null }, ct)
            .ConfigureAwait(false);
        var draft = await writer.WriteAsync(topic, mandate, research, ct).ConfigureAwait(false);
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

        if (project.Scenes.Count == 0)
        {
            var script = project.Script ?? throw new InvalidOperationException(NoScript);
            var mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
            var planned = await planner.PlanAsync(script, mandate, ct).ConfigureAwait(false);
            project = await SaveProductionAsync(id, current => current.Scenes.Count == 0 ? current with { Scenes = planned } : current, ct)
                .ConfigureAwait(false);
            if (project is null)
            {
                return false;
            }
        }

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
            if (scene.Locked || !NeedsMedia(scene, directory))
            {
                continue;
            }

            NarrationResult? audio;
            VisualResult? visual;
            try
            {
                audio = scene.AudioSource == Owner ? null : await narration.GenerateAsync(scene, directory, ct).ConfigureAwait(false);
                visual = scene.VisualSource == Owner ? null : await visuals.GenerateAsync(scene, position, directory, ct).ConfigureAwait(false);
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

        return await SaveProductionAsync(id, current => current with { Status = MediaReady, Error = null }, ct)
            .ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Save a change to the project as the control plane holds it now, read immediately before; null, and nothing saved,
    /// when the project is no longer producing, read so or refused so by the control plane.
    /// </summary>
    private async Task<OperatorProjectState?> SaveProductionAsync(
        string id, Func<OperatorProjectState, OperatorProjectState> change, CancellationToken ct)
    {
        var current = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
        if (current.Status != Producing)
        {
            return null;
        }

        try
        {
            return await controlPlane.SaveProjectAsync(change(current), null, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message == NoLongerProducing)
        {
            return null;
        }
    }

    private static bool NeedsMedia(OperatorScene scene, string directory)
        => scene.Status != Ready
            || (scene.AudioSource == Generated && (scene.AudioFile is null || !MediaFiles.Exists(directory, scene.AudioFile)))
            || (scene.VisualSource == Generated && (scene.VisualFile is null || !MediaFiles.Exists(directory, scene.VisualFile)));

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
            next = next with { VisualSource = visual.VisualSource, VisualFile = visual.VisualFile };
        }

        return next;
    }

    private static OperatorProjectState WithScene(OperatorProjectState current, string sceneId, Func<OperatorScene, OperatorScene> change)
        => current with { Scenes = [.. current.Scenes.Select(s => s.Id == sceneId && !s.Locked ? change(s) : s)] };

    /// <summary>Save the project as failed with <paramref name="error"/>, starting from the control plane's copy.</summary>
    public Task SaveFailedAsync(string id, string error, CancellationToken ct)
        => SaveAsync(id, current => current with { Status = Failed, Error = error }, ct);

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

    private async Task NotifyAsync(string? originReference, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(originReference))
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
