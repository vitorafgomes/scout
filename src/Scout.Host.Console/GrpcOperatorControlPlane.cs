using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.Operator;

namespace Scout.Host.Console;

/// <summary>
/// <see cref="IOperatorControlPlane"/> over the control plane's <c>WorkerGate</c> service, on the channel, the
/// worker token and the deadline of the <see cref="GrpcWriteGate"/> it is given. A refusal the control plane
/// words (no grant, a value it does not accept, a change only the owner may make, an unknown project) keeps its
/// words; any other failure reads "Scout could not reach its control plane."
/// </summary>
internal sealed class GrpcOperatorControlPlane(GrpcWriteGate gate) : IOperatorControlPlane
{
    internal const string Unreachable = "Scout could not reach its control plane.";

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
            throw new InvalidOperationException(
                worded && !string.IsNullOrWhiteSpace(ex.Status.Detail) ? ex.Status.Detail : Unreachable, ex);
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
