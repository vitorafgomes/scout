using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.Operator;

namespace Scout.Host.Console;

/// <summary>
/// <see cref="IOperatorControlPlane"/> over the control plane's <c>WorkerGate</c> service, on the channel, the
/// worker token and the deadline of the <see cref="GrpcWriteGate"/> it is given. A refusal the control plane
/// words (no grant, a value it does not accept, a change only the owner may make, an unknown project) keeps its
/// words; any other failure reads "Scout could not reach its control plane." A record the control plane already logged
/// is a success, and a record refused because the owner revoked the grant is a <see cref="WriteRevokedException"/>.
/// </summary>
internal sealed class GrpcOperatorControlPlane(GrpcWriteGate gate) : IOperatorControlPlane
{
    internal const string Unreachable = "Scout could not reach its control plane.";

    internal const string AlreadyRecordedPhrase = "already recorded in the write log";

    internal const string RevokedPhrase = "revoked";

    public async Task<OperatorMandate> GetMandateAsync(CancellationToken ct)
    {
        var mandate = await CallAsync(
            () => gate.Client.GetOperatorMandateAsync(new GetMandateRequest(), gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return new OperatorMandate(
            mandate.Objective,
            mandate.Audience,
            [.. mandate.Pillars],
            mandate.Cadence,
            mandate.Language,
            mandate.VoiceAndStyle,
            [.. mandate.BlockedTopics],
            mandate.TargetMetric,
            mandate.TargetValue,
            mandate.HasUpdatedAt ? mandate.UpdatedAt : null);
    }

    public async Task<OperatorProjectState> SaveProjectAsync(
        OperatorProjectState project, string? originReference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var request = new SaveOperatorProjectRequest { Project = ToWire(project) };
        if (originReference is not null)
        {
            request.OriginReference = originReference;
        }

        var saved = await CallAsync(
            () => gate.Client.SaveOperatorProjectAsync(request, gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return FromWire(saved);
    }

    public async Task<QueuedProject?> NextQueuedAsync(CancellationToken ct)
    {
        var next = await CallAsync(
            () => gate.Client.NextQueuedProjectAsync(new NextQueuedProjectRequest(), gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return next.Project is null
            ? null
            : new QueuedProject(FromWire(next.Project), next.HasOriginReference ? next.OriginReference : null);
    }

    public async Task<OperatorProjectState> GetProjectAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var project = await CallAsync(
            () => gate.Client.GetOperatorProjectAsync(new GetProjectRequest { Id = id }, gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return FromWire(project);
    }

    public async Task<IReadOnlyList<OperatorProjectState>> ListRecentAsync(int limit, CancellationToken ct)
    {
        var list = await CallAsync(
            () => gate.Client.ListOperatorProjectsAsync(new ListProjectsRequest { Limit = limit }, gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return [.. list.Projects.Select(FromWire)];
    }

    private static async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (RpcException ex) when (!ct.IsCancellationRequested)
        {
            var worded = ex.StatusCode is StatusCode.PermissionDenied or StatusCode.FailedPrecondition
                or StatusCode.InvalidArgument or StatusCode.NotFound;
            throw worded && !string.IsNullOrWhiteSpace(ex.Status.Detail)
                ? new InvalidOperationException(ex.Status.Detail, ex)
                : new ControlPlaneUnavailableException(Unreachable, ex);
        }
    }

    private static Project ToWire(OperatorProjectState project)
    {
        var wire = new Project
        {
            Id = project.Id,
            Topic = project.Topic,
            Status = project.Status,
            Origin = project.Origin,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
        };
        wire.Candidates.AddRange(project.Candidates.Select(c => new TopicCandidate { Title = c.Title, Reason = c.Reason, Fit = c.Fit }));
        if (project.Script is { } script)
        {
            var wireScript = new Script { Hook = script.Hook, CallToAction = script.CallToAction, Description = script.Description };
            wireScript.Sections.AddRange(script.Sections.Select(s => new ScriptSection { Heading = s.Heading, Narration = s.Narration }));
            wireScript.TitleVariants.AddRange(script.TitleVariants);
            wireScript.Tags.AddRange(script.Tags);
            wireScript.Chapters.AddRange(script.Chapters.Select(c => new Chapter { At = c.At, Title = c.Title }));
            wire.Script = wireScript;
        }

        wire.Claims.AddRange(project.Claims.Select(c =>
        {
            var claim = new Claim { Id = c.Id, Text = c.Text, Risk = c.Risk, Status = c.Status };
            claim.SourceUrls.AddRange(c.SourceUrls);
            if (c.Note is not null)
            {
                claim.Note = c.Note;
            }

            return claim;
        }));
        if (project.Error is not null)
        {
            wire.Error = project.Error;
        }

        wire.Scenes.AddRange(project.Scenes.Select(ToWire));
        if (project.Package is { } package)
        {
            wire.Package = ToWire(package);
        }

        if (project.Publication is { } publication)
        {
            wire.Publication = ToWire(publication);
        }

        return wire;
    }

    private static Scene ToWire(OperatorScene scene)
    {
        var wire = new Scene
        {
            Id = scene.Id,
            Order = scene.Order,
            Narration = scene.Narration,
            VisualPrompt = scene.VisualPrompt,
            Locked = scene.Locked,
            Status = scene.Status,
            AudioSource = scene.AudioSource,
            VisualSource = scene.VisualSource,
            DurationMs = scene.DurationMs,
        };
        if (scene.Error is not null)
        {
            wire.Error = scene.Error;
        }

        if (scene.AudioFile is not null)
        {
            wire.AudioFile = scene.AudioFile;
        }

        if (scene.VisualFile is not null)
        {
            wire.VisualFile = scene.VisualFile;
        }

        return wire;
    }

    private static OperatorProjectState FromWire(Project wire)
        => new(
            wire.Id,
            wire.Topic,
            wire.Status,
            wire.Origin,
            wire.CreatedAt,
            wire.UpdatedAt,
            [.. wire.Candidates.Select(c => new OperatorTopicCandidate(c.Title, c.Reason, c.Fit))],
            wire.Script is null
                ? null
                : new OperatorScript(
                    wire.Script.Hook,
                    [.. wire.Script.Sections.Select(s => new OperatorScriptSection(s.Heading, s.Narration))],
                    wire.Script.CallToAction,
                    [.. wire.Script.TitleVariants],
                    wire.Script.Description,
                    [.. wire.Script.Tags],
                    [.. wire.Script.Chapters.Select(c => new OperatorChapter(c.At, c.Title))]),
            [.. wire.Claims.Select(c => new OperatorClaim(c.Id, c.Text, c.Risk, [.. c.SourceUrls], c.Status, c.HasNote ? c.Note : null))],
            wire.HasError ? wire.Error : null)
        {
            Scenes = [.. wire.Scenes.Select(s => new OperatorScene(
                s.Id,
                s.Order,
                s.Narration,
                s.VisualPrompt,
                s.Locked,
                s.Status,
                s.HasError ? s.Error : null,
                s.AudioSource,
                s.VisualSource,
                s.HasAudioFile ? s.AudioFile : null,
                s.HasVisualFile ? s.VisualFile : null,
                s.DurationMs))],
            Package = wire.Package is null ? null : FromWire(wire.Package),
            Publication = wire.Publication is null ? null : FromWire(wire.Publication),
        };

    public async Task<string> GetMediaDirectoryAsync(string projectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var directory = await CallAsync(
            () => gate.Client.GetOperatorMediaDirectoryAsync(new GetProjectRequest { Id = projectId }, gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return directory.Path;
    }

    public async Task ReportReadinessAsync(OperatorReadiness readiness, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        var wire = new Readiness
        {
            SpeechAvailable = readiness.SpeechAvailable,
            ImagesAvailable = readiness.ImagesAvailable,
            Provider = readiness.Provider,
            FfmpegAvailable = readiness.FfmpegAvailable,
            YoutubeSignedIn = readiness.YouTubeSignedIn,
        };
        wire.Notes.AddRange(readiness.Notes);
        await CallAsync(() => gate.Client.ReportReadinessAsync(wire, gate.CallOptions(ct)).ResponseAsync, ct).ConfigureAwait(false);
    }

    public async Task<OperatorAction?> NextApprovedActionAsync(CancellationToken ct)
    {
        var next = await CallAsync(
            () => gate.Client.NextApprovedActionAsync(new NextApprovedActionRequest(), gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return next.Action is not { } action
            ? null
            : new OperatorAction(action.Id, action.FeatureId, action.Action, action.Target, action.Summary, next.HasOriginReference ? next.OriginReference : null);
    }

    public async Task RecordActionAsync(OperatorAction action, string outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        var request = new RecordWriteRequest
        {
            FeatureId = action.FeatureId,
            Scope = action.Action,
            Target = action.Target,
            Outcome = outcome,
            ActionId = action.Id,
        };
        try
        {
            await CallAsync(() => gate.Client.RecordWriteAsync(request, gate.CallOptions(ct)).ResponseAsync, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex is not ControlPlaneUnavailableException
            && ex.Message.Contains(AlreadyRecordedPhrase, StringComparison.Ordinal))
        {
        }
        catch (InvalidOperationException ex) when (ex is not ControlPlaneUnavailableException
            && ex.Message.Contains(RevokedPhrase, StringComparison.Ordinal))
        {
            throw new WriteRevokedException(ex.Message, ex);
        }
    }

    private static Publication ToWire(OperatorPublication publication)
    {
        var wire = new Publication
        {
            Status = publication.Status,
            PrivacyStatus = publication.PrivacyStatus,
            MadeForKids = publication.MadeForKids,
            SyntheticMedia = publication.SyntheticMedia,
        };
        if (publication.VideoId is not null)
        {
            wire.VideoId = publication.VideoId;
        }

        if (publication.PublishAt is not null)
        {
            wire.PublishAt = publication.PublishAt;
        }

        if (publication.UploadedAt is not null)
        {
            wire.UploadedAt = publication.UploadedAt;
        }

        if (publication.Error is not null)
        {
            wire.Error = publication.Error;
        }

        wire.Snapshots.AddRange(publication.Snapshots.Select(ToWire));
        wire.Proposals.AddRange(publication.Proposals.Select(p =>
        {
            var proposal = new Proposal { Id = p.Id, Kind = p.Kind, Text = p.Text, Reason = p.Reason, Status = p.Status };
            if (p.ThumbnailFile is not null)
            {
                proposal.ThumbnailFile = p.ThumbnailFile;
            }

            return proposal;
        }));
        if (publication.Experiment is { } experiment)
        {
            wire.Experiment = ToWire(experiment);
        }

        return wire;
    }

    private static Snapshot ToWire(OperatorSnapshot snapshot)
    {
        var wire = new Snapshot
        {
            Window = snapshot.Window,
            TakenAt = snapshot.TakenAt,
            Views = snapshot.Views,
            MinutesWatched = snapshot.MinutesWatched,
            AverageViewDurationSeconds = snapshot.AverageViewDurationSeconds,
            AverageViewPercentage = snapshot.AverageViewPercentage,
            SubscribersGained = snapshot.SubscribersGained,
            SubscribersLost = snapshot.SubscribersLost,
            Likes = snapshot.Likes,
            Comments = snapshot.Comments,
            Shares = snapshot.Shares,
        };
        if (snapshot.Impressions is { } impressions)
        {
            wire.Impressions = impressions;
        }

        if (snapshot.ImpressionsCtr is { } ctr)
        {
            wire.ImpressionsCtr = ctr;
        }

        wire.Retention.AddRange(snapshot.Retention.Select(r => new RetentionPoint { ElapsedRatio = r.ElapsedRatio, AudienceWatchRatio = r.AudienceWatchRatio }));
        return wire;
    }

    private static Experiment ToWire(OperatorExperiment experiment)
    {
        var wire = new Experiment { Id = experiment.Id, Kind = experiment.Kind, Status = experiment.Status };
        if (experiment.WinnerArmId is not null)
        {
            wire.WinnerArmId = experiment.WinnerArmId;
        }

        if (experiment.Result is not null)
        {
            wire.Result = experiment.Result;
        }

        wire.Arms.AddRange(experiment.Arms.Select(a =>
        {
            var arm = new ExperimentArm
            {
                Id = a.Id,
                Label = a.Label,
                Value = a.Value,
                Impressions = a.Impressions,
                Clicks = a.Clicks,
                AverageViewPercentage = a.AverageViewPercentage,
            };
            if (a.StartedAt is not null)
            {
                arm.StartedAt = a.StartedAt;
            }

            if (a.EndedAt is not null)
            {
                arm.EndedAt = a.EndedAt;
            }

            return arm;
        }));
        return wire;
    }

    private static OperatorPublication FromWire(Publication wire)
        => new(
            wire.Status,
            wire.HasVideoId ? wire.VideoId : null,
            wire.PrivacyStatus,
            wire.HasPublishAt ? wire.PublishAt : null,
            wire.MadeForKids,
            wire.SyntheticMedia,
            wire.HasUploadedAt ? wire.UploadedAt : null,
            wire.HasError ? wire.Error : null,
            [.. wire.Snapshots.Select(s => new OperatorSnapshot(
                s.Window,
                s.TakenAt,
                s.Views,
                s.MinutesWatched,
                s.AverageViewDurationSeconds,
                s.AverageViewPercentage,
                s.SubscribersGained,
                s.SubscribersLost,
                s.Likes,
                s.Comments,
                s.Shares,
                s.HasImpressions ? s.Impressions : null,
                s.HasImpressionsCtr ? s.ImpressionsCtr : null,
                [.. s.Retention.Select(r => new OperatorRetentionPoint(r.ElapsedRatio, r.AudienceWatchRatio))]))],
            [.. wire.Proposals.Select(p => new OperatorProposal(p.Id, p.Kind, p.Text, p.Reason, p.Status, p.HasThumbnailFile ? p.ThumbnailFile : null))],
            wire.Experiment is not { } experiment
                ? null
                : new OperatorExperiment(
                    experiment.Id,
                    experiment.Kind,
                    experiment.Status,
                    [.. experiment.Arms.Select(a => new OperatorExperimentArm(
                        a.Id,
                        a.Label,
                        a.Value,
                        a.HasStartedAt ? a.StartedAt : null,
                        a.HasEndedAt ? a.EndedAt : null,
                        a.Impressions,
                        a.Clicks,
                        a.AverageViewPercentage))],
                    experiment.HasWinnerArmId ? experiment.WinnerArmId : null,
                    experiment.HasResult ? experiment.Result : null));

    private static VideoPackage ToWire(OperatorPackage package)
    {
        var wire = new VideoPackage
        {
            Status = package.Status,
            RightsConfirmed = package.RightsConfirmed,
            Title = package.Title,
            Description = package.Description,
            DurationMs = package.DurationMs,
            MetadataEdited = package.MetadataEdited,
        };
        if (package.VideoFile is not null)
        {
            wire.VideoFile = package.VideoFile;
        }

        if (package.ThumbnailFile is not null)
        {
            wire.ThumbnailFile = package.ThumbnailFile;
        }

        if (package.ApprovedAt is not null)
        {
            wire.ApprovedAt = package.ApprovedAt;
        }

        if (package.Error is not null)
        {
            wire.Error = package.Error;
        }

        wire.Shorts.AddRange(package.Shorts.Select(s => new ShortClip { File = s.File, StartMs = s.StartMs, DurationMs = s.DurationMs, Hook = s.Hook }));
        wire.Checks.AddRange(package.Checks.Select(c => new PackageCheck { Name = c.Name, Result = c.Result, Detail = c.Detail }));
        wire.Tags.AddRange(package.Tags);
        return wire;
    }

    private static OperatorPackage FromWire(VideoPackage wire)
        => new(
            wire.Status,
            wire.HasVideoFile ? wire.VideoFile : null,
            [.. wire.Shorts.Select(s => new OperatorShort(s.File, s.StartMs, s.DurationMs, s.Hook))],
            wire.HasThumbnailFile ? wire.ThumbnailFile : null,
            [.. wire.Checks.Select(c => new OperatorCheck(c.Name, c.Result, c.Detail))],
            wire.RightsConfirmed,
            wire.HasApprovedAt ? wire.ApprovedAt : null,
            wire.Title,
            wire.Description,
            [.. wire.Tags],
            wire.DurationMs,
            wire.HasError ? wire.Error : null,
            wire.MetadataEdited);
}
