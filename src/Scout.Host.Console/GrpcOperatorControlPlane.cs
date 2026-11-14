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
/// A call whose token was cancelled, as when the worker stops, ends as an <see cref="OperationCanceledException"/> carrying that token.
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
        catch (RpcException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Status.Detail, ex, ct);
        }
        catch (RpcException ex)
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
            Delegated = project.Delegated,
            NarrationVoice = project.NarrationVoice,
            Direction = project.Direction,
            Motion = project.Motion,
            Format = project.Format,
            RecordingMs = project.RecordingMs,
            Presenter = project.Presenter,
            PresenterRequest = project.PresenterRequest,
            PresenterSource = project.PresenterSource,
            PresenterReturnStatus = project.PresenterReturnStatus,
            PresenterRequestPrompt = project.PresenterRequestPrompt,
        };
        wire.Candidates.AddRange(project.Candidates.Select(c => new TopicCandidate { Title = c.Title, Reason = c.Reason, Fit = c.Fit }));
        if (project.Script is { } script)
        {
            var wireScript = new Script { Hook = script.Hook, CallToAction = script.CallToAction, Description = script.Description };
            wireScript.Sections.AddRange(script.Sections.Select(s => new ScriptSection { Heading = s.Heading, Narration = s.Narration, StartMs = s.StartMs, EndMs = s.EndMs }));
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

        if (project.BackgroundFile is not null)
        {
            wire.BackgroundFile = project.BackgroundFile;
        }

        if (project.RecordingFile is not null)
        {
            wire.RecordingFile = project.RecordingFile;
        }

        if (project.PresenterFile is not null)
        {
            wire.PresenterFile = project.PresenterFile;
        }

        if (project.MouthBox is { } mouth)
        {
            wire.MouthBox = ToWire(mouth);
        }

        if (project.PresenterPreviewFile is not null)
        {
            wire.PresenterPreviewFile = project.PresenterPreviewFile;
        }

        wire.PresenterVersions.AddRange(project.PresenterVersions.Select(ToWire));
        return wire;
    }

    private static MouthBox ToWire(OperatorMouthBox mouth)
        => new() { X = mouth.X, Y = mouth.Y, Width = mouth.Width, Height = mouth.Height };

    private static OperatorMouthBox? FromWire(MouthBox? mouth)
        => mouth is null ? null : new OperatorMouthBox(mouth.X, mouth.Y, mouth.Width, mouth.Height);

    private static PresenterVersion ToWire(OperatorPresenterVersion version)
    {
        var wire = new PresenterVersion
        {
            File = version.File,
            Prompt = version.Prompt,
            Feedback = version.Feedback,
            MadeAt = version.MadeAt,
            Source = version.Source,
        };
        if (version.PreviewFile is not null)
        {
            wire.PreviewFile = version.PreviewFile;
        }

        if (version.MouthBox is { } mouth)
        {
            wire.MouthBox = ToWire(mouth);
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
            VisualQuery = scene.VisualQuery,
            ClipStartMs = scene.ClipStartMs,
            ClipEndMs = scene.ClipEndMs,
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

        if (scene.VisualCredit is not null)
        {
            wire.VisualCredit = scene.VisualCredit;
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
                    [.. wire.Script.Sections.Select(s => new OperatorScriptSection(s.Heading, s.Narration) { StartMs = s.StartMs, EndMs = s.EndMs })],
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
                s.DurationMs)
            {
                VisualQuery = s.VisualQuery,
                VisualCredit = s.HasVisualCredit ? s.VisualCredit : null,
                ClipStartMs = s.ClipStartMs,
                ClipEndMs = s.ClipEndMs,
            })],
            Package = wire.Package is null ? null : FromWire(wire.Package),
            Publication = wire.Publication is null ? null : FromWire(wire.Publication),
            BackgroundFile = wire.HasBackgroundFile ? wire.BackgroundFile : null,
            Delegated = wire.Delegated,
            NarrationVoice = wire.NarrationVoice,
            Direction = wire.Direction,
            Motion = wire.Motion,
            Format = wire.Format,
            RecordingFile = wire.HasRecordingFile ? wire.RecordingFile : null,
            RecordingMs = wire.RecordingMs,
            Presenter = wire.Presenter,
            PresenterFile = wire.HasPresenterFile ? wire.PresenterFile : null,
            MouthBox = FromWire(wire.MouthBox),
            PresenterVersions = [.. wire.PresenterVersions.Select(v => new OperatorPresenterVersion(
                v.File, v.Prompt, v.Feedback, v.HasPreviewFile ? v.PreviewFile : null, FromWire(v.MouthBox), v.MadeAt, v.Source))],
            PresenterPreviewFile = wire.HasPresenterPreviewFile ? wire.PresenterPreviewFile : null,
            PresenterRequest = wire.PresenterRequest,
            PresenterSource = wire.PresenterSource,
            PresenterReturnStatus = wire.PresenterReturnStatus,
            PresenterRequestPrompt = wire.PresenterRequestPrompt,
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
            StockPictures = readiness.StockPictures,
            TranscriptionAvailable = readiness.TranscriptionAvailable,
            LipSyncAvailable = readiness.LipSyncAvailable,
            FaceDetectionAvailable = readiness.FaceDetectionAvailable,
            PicturesLeftToday = readiness.PicturesLeftToday,
        };
        wire.Notes.AddRange(readiness.Notes);
        wire.BuiltinPresenters.AddRange(readiness.BuiltInPresenters.Select(p => new BuiltInPresenter { Id = p.Id, Label = p.Label }));
        wire.Voices.AddRange(readiness.Voices.Select(ToWire));
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

    public async Task<bool> CheckGrantAsync(OperatorAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        var reply = await CallAsync(
                () => gate.Client.CheckActionGrantAsync(
                    new CheckActionGrantRequest { FeatureId = action.FeatureId, ActionId = action.Id }, gate.CallOptions(ct)).ResponseAsync,
                ct)
            .ConfigureAwait(false);
        return reply.Granted;
    }

    public async Task<OperatorComments> GetCommentsAsync(CancellationToken ct)
    {
        var view = await CallAsync(
            () => gate.Client.GetCommentsAsync(new ListCommentsRequest(), gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return FromWire(view);
    }

    public async Task<OperatorComments> SaveCommentsAsync(OperatorComments comments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(comments);
        var wire = ToWire(comments);
        var saved = await CallAsync(() => gate.Client.SaveCommentsAsync(wire, gate.CallOptions(ct)).ResponseAsync, ct).ConfigureAwait(false);
        return FromWire(saved);
    }

    private static CommentsView ToWire(OperatorComments comments)
    {
        var wire = new CommentsView();
        wire.Comments.AddRange(comments.Comments.Select(c =>
        {
            var comment = new AudienceComment
            {
                Id = c.Id,
                ThreadId = c.ThreadId,
                VideoId = c.VideoId,
                VideoTitle = c.VideoTitle,
                Author = c.Author,
                Text = c.Text,
                PublishedAt = c.PublishedAt,
                LikeCount = c.LikeCount,
                Kind = c.Kind,
                Theme = c.Theme,
                Status = c.Status,
                Draft = c.Draft,
                FetchedAt = c.FetchedAt,
            };
            if (c.ApprovedText is not null)
            {
                comment.ApprovedText = c.ApprovedText;
            }

            if (c.ReplyId is not null)
            {
                comment.ReplyId = c.ReplyId;
            }

            if (c.Error is not null)
            {
                comment.Error = c.Error;
            }

            if (c.DecidedAt is not null)
            {
                comment.DecidedAt = c.DecidedAt;
            }

            return comment;
        }));
        wire.Ideas.AddRange(comments.Ideas.Select(i =>
        {
            var idea = new AudienceIdea { Id = i.Id, Text = i.Text, CommentId = i.CommentId, Status = i.Status };
            if (i.ProjectId is not null)
            {
                idea.ProjectId = i.ProjectId;
            }

            return idea;
        }));
        if (comments.SyncedAt is not null)
        {
            wire.SyncedAt = comments.SyncedAt;
        }

        if (comments.SyncRequestedAt is not null)
        {
            wire.SyncRequestedAt = comments.SyncRequestedAt;
        }

        if (comments.SyncError is not null)
        {
            wire.SyncError = comments.SyncError;
        }

        return wire;
    }

    private static OperatorComments FromWire(CommentsView wire)
        => new(
            [.. wire.Comments.Select(c => new OperatorComment(
                c.Id,
                c.ThreadId,
                c.VideoId,
                c.VideoTitle,
                c.Author,
                c.Text,
                c.PublishedAt,
                c.LikeCount,
                c.Kind,
                c.Theme,
                c.Status,
                c.Draft,
                c.HasApprovedText ? c.ApprovedText : null,
                c.HasReplyId ? c.ReplyId : null,
                c.HasError ? c.Error : null,
                c.HasDecidedAt ? c.DecidedAt : null,
                c.FetchedAt))],
            [.. wire.Ideas.Select(i => new OperatorIdea(i.Id, i.Text, i.CommentId, i.Status, i.HasProjectId ? i.ProjectId : null))],
            wire.HasSyncedAt ? wire.SyncedAt : null,
            wire.HasSyncRequestedAt ? wire.SyncRequestedAt : null,
            wire.HasSyncError ? wire.SyncError : null);

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

        if (package.AssembledAt is not null)
        {
            wire.AssembledAt = package.AssembledAt;
        }

        if (package.MetadataSavedAt is not null)
        {
            wire.MetadataSavedAt = package.MetadataSavedAt;
        }

        if (package.RightsConfirmedAt is not null)
        {
            wire.RightsConfirmedAt = package.RightsConfirmedAt;
        }

        if (package.ThumbnailDescription is not null)
        {
            wire.ThumbnailDescription = package.ThumbnailDescription;
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
            wire.MetadataEdited)
        {
            AssembledAt = wire.HasAssembledAt ? wire.AssembledAt : null,
            MetadataSavedAt = wire.HasMetadataSavedAt ? wire.MetadataSavedAt : null,
            RightsConfirmedAt = wire.HasRightsConfirmedAt ? wire.RightsConfirmedAt : null,
            ThumbnailDescription = wire.HasThumbnailDescription ? wire.ThumbnailDescription : null,
        };

    public async Task<string> GetVoiceSamplesDirectoryAsync(CancellationToken ct)
    {
        var directory = await CallAsync(
            () => gate.Client.GetVoiceSamplesDirectoryAsync(new GetVoiceSamplesDirectoryRequest(), gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return directory.Path;
    }

    public Task<OperatorProjectState> EditSceneTextAsync(string projectId, string sceneId, string text, CancellationToken ct)
        => ChatAsync(() => gate.Client.ChatEditSceneTextAsync(
            new EditSceneTextRequest { ProjectId = projectId, SceneId = sceneId, Text = text }, gate.CallOptions(ct)).ResponseAsync, ct);

    public Task<OperatorProjectState> RegenerateNarrationAsync(string projectId, string sceneId, CancellationToken ct)
        => ChatAsync(() => gate.Client.ChatRegenerateNarrationAsync(
            new SceneRequest { ProjectId = projectId, SceneId = sceneId }, gate.CallOptions(ct)).ResponseAsync, ct);

    public Task<OperatorProjectState> RegenerateSceneAsync(string projectId, string sceneId, CancellationToken ct)
        => ChatAsync(() => gate.Client.ChatRegenerateSceneAsync(
            new SceneRequest { ProjectId = projectId, SceneId = sceneId }, gate.CallOptions(ct)).ResponseAsync, ct);

    public Task<OperatorProjectState> SetSceneLockedAsync(string projectId, string sceneId, bool locked, CancellationToken ct)
        => ChatAsync(() => gate.Client.ChatSetSceneLockedAsync(
            new SetSceneLockedRequest { ProjectId = projectId, SceneId = sceneId, Locked = locked }, gate.CallOptions(ct)).ResponseAsync, ct);

    public Task<OperatorProjectState> ReorderScenesAsync(string projectId, IReadOnlyList<string> sceneIds, CancellationToken ct)
    {
        var request = new ReorderScenesRequest { ProjectId = projectId };
        request.SceneIds.AddRange(sceneIds);
        return ChatAsync(() => gate.Client.ChatReorderScenesAsync(request, gate.CallOptions(ct)).ResponseAsync, ct);
    }

    public Task<OperatorProjectState> ProduceProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct)
        => ChatAsync(() => gate.Client.ChatProduceProjectAsync(
            new ProduceProjectRequest { Id = projectId, NarrationVoice = narrationVoice, Direction = direction, Motion = motion },
            gate.CallOptions(ct)).ResponseAsync, ct);

    public Task<OperatorProjectState> DelegateProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct)
        => ChatAsync(() => gate.Client.ChatDelegateProjectAsync(
            new DelegateProjectRequest { Id = projectId, NarrationVoice = narrationVoice, Direction = direction, Motion = motion },
            gate.CallOptions(ct)).ResponseAsync, ct);

    public Task<OperatorProjectState> RequestPresenterAsync(string projectId, string feedback, string prompt, CancellationToken ct)
        => ChatAsync(() => gate.Client.ChatRequestPresenterAsync(
            new RequestPresenterRequest { ProjectId = projectId, Feedback = feedback, Prompt = prompt }, gate.CallOptions(ct)).ResponseAsync, ct);

    private static async Task<OperatorProjectState> ChatAsync(Func<Task<Project>> call, CancellationToken ct)
        => FromWire(await CallAsync(call, ct).ConfigureAwait(false));

    private static Voice ToWire(OperatorVoice voice)
    {
        var wire = new Voice { Id = voice.Id, Label = voice.Label, Source = voice.Source };
        if (voice.SampleFile is not null)
        {
            wire.SampleFile = voice.SampleFile;
        }

        return wire;
    }
}
