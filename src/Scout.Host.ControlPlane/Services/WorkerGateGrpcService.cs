using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Controls;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using WireMandate = Scout.ControlPlane.Contracts.Mandate;
using WireReadiness = Scout.ControlPlane.Contracts.Readiness;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over <see cref="WriteGateService"/> and the worker side of
/// <see cref="OperatorService"/>, reachable with the worker token only. A write request goes through
/// <see cref="WriteGateService.RequestToRunAsync"/>, so a reversible write is in the write log before the worker runs it, and
/// the allowed answer carries that entry's id; when the entry cannot be written the call fails. A write that cannot be
/// recorded answers <see cref="StatusCode.FailedPrecondition"/>; the next approved action answers an empty
/// response when none is due or the owner has not granted YouTube, and carries its project's origin reference,
/// like the next queued project, only when the project has one. Just before it sends a claimed action, the worker asks
/// <see cref="WriteGateService.CheckGrantAsync"/> whether the grant still holds. The worker reads and saves the comments through
/// <see cref="CommentService"/>, and carries the owner's chat edits of a project (scene text, narration, scene,
/// lock, order, production, delegation, a new presenter, a scene's picture prompt and picture) to the chat methods of <see cref="OperatorService"/>, each under the
/// owner's YouTube grant and logged as done via chat. A write request whose group id or repository is empty or whitespace
/// is queued as one without that field, so no action lands in a group no set call can name. The worker reports that a request ended by closing its group, which
/// decides nothing; a close without a group id answers <see cref="StatusCode.InvalidArgument"/>.
/// </summary>
public sealed class WorkerGateGrpcService(WriteGateService gate, OperatorService @operator, CommentService comments, OwnerControlService controls)
    : WorkerGate.WorkerGateBase
{
    private readonly WriteGateService _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly OperatorService _operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
    private readonly CommentService _comments = comments ?? throw new ArgumentNullException(nameof(comments));
    private readonly OwnerControlService _controls = controls ?? throw new ArgumentNullException(nameof(controls));

    public override async Task<RequestWriteResponse> RequestWrite(
        RequestWriteRequest request,
        ServerCallContext context)
    {
        var decision = await _gate.RequestToRunAsync(
                request.FeatureId,
                request.Scope,
                request.Target,
                request.Summary,
                request.HasPayload ? request.Payload : null,
                string.IsNullOrWhiteSpace(request.GroupId) ? null : request.GroupId,
                string.IsNullOrWhiteSpace(request.Repository) ? null : request.Repository,
                context.CancellationToken)
            .ConfigureAwait(false);

        var response = new RequestWriteResponse
        {
            Decision = decision.Kind.ToString().ToLowerInvariant(),
            Reason = decision.Reason,
        };
        if (decision.ActionId is not null)
        {
            response.ActionId = decision.ActionId;
        }
        return response;
    }

    public override async Task<CloseWriteGroupResponse> CloseWriteGroup(
        CloseWriteGroupRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.GroupId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A group is closed by its id; none was given."));
        }

        return new CloseWriteGroupResponse
        {
            Actions = await _gate.CloseGroupAsync(request.GroupId, context.CancellationToken).ConfigureAwait(false),
        };
    }

    public override async Task<RecordWriteResponse> RecordWrite(
        RecordWriteRequest request,
        ServerCallContext context)
    {
        try
        {
            await _gate.RecordAsync(
                    request.FeatureId,
                    request.Scope,
                    request.Target,
                    request.Outcome,
                    request.HasActionId ? request.ActionId : null,
                    request.HasResult ? request.Result : null,
                    request.HasSignedAs ? request.SignedAs : null,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (WriteNotRecordableException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }

        return new RecordWriteResponse();
    }

    public override Task<WireMandate> GetOperatorMandate(GetMandateRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () =>
            OperatorMapping.ToWire(await _operator.GetMandateAsync(context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> SaveOperatorProject(SaveOperatorProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.SaveProjectFromWorkerAsync(
                    OperatorMapping.ToDomain(request.Project ?? new Project()),
                    request.HasOriginReference ? request.OriginReference : null,
                    context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<NextQueuedProjectResponse> NextQueuedProject(
        NextQueuedProjectRequest request,
        ServerCallContext context) =>
        OperatorMapping.Guard(async () =>
        {
            var next = await _operator.NextQueuedProjectAsync(context.CancellationToken).ConfigureAwait(false);
            var response = new NextQueuedProjectResponse();
            if (next is not null)
            {
                response.Project = OperatorMapping.ToWire(next);
                if (next.OriginReference is not null)
                {
                    response.OriginReference = next.OriginReference;
                }
            }
            return response;
        });

    public override Task<Project> GetOperatorProject(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.GetProjectForWorkerAsync(request.Id, context.CancellationToken).ConfigureAwait(false)));

    public override Task<ListProjectsResponse> ListOperatorProjects(ListProjectsRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () =>
        {
            var projects = await _operator.ListProjectsForWorkerAsync(request.Limit, context.CancellationToken)
                .ConfigureAwait(false);
            var response = new ListProjectsResponse();
            response.Projects.AddRange(projects.Select(OperatorMapping.ToWire));
            return response;
        });

    public override Task<MediaDirectory> GetOperatorMediaDirectory(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => new MediaDirectory
        {
            Path = await _operator.GetMediaDirectoryForWorkerAsync(request.Id, context.CancellationToken).ConfigureAwait(false),
        });

    public override Task<WireReadiness> ReportReadiness(WireReadiness request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.ReportReadinessAsync(OperatorMapping.ToDomain(request), context.CancellationToken)
                .ConfigureAwait(false)));

    public override async Task<NextApprovedActionResponse> NextApprovedAction(
        NextApprovedActionRequest request,
        ServerCallContext context)
    {
        var response = new NextApprovedActionResponse();
        if (!string.IsNullOrWhiteSpace(request.FeatureId) && !string.Equals(request.FeatureId, FeatureCatalog.YouTubeId, StringComparison.Ordinal))
        {
            var claimed = await _gate.ClaimNextApprovedAsync(request.FeatureId, context.CancellationToken).ConfigureAwait(false);
            if (claimed is not null)
            {
                response.Action = FeatureMapping.ToWire(claimed);
                if (claimed.Payload is not null)
                {
                    response.Payload = claimed.Payload;
                }
            }

            return response;
        }

        var action = await _operator.NextApprovedActionAsync(context.CancellationToken).ConfigureAwait(false);
        if (action is not null)
        {
            response.Action = FeatureMapping.ToWire(action);
            if (await _operator.OriginReferenceForWorkerAsync(action, context.CancellationToken).ConfigureAwait(false) is { } reference)
            {
                response.OriginReference = reference;
            }
        }
        return response;
    }

    public override async Task<CheckActionGrantResponse> CheckActionGrant(
        CheckActionGrantRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.FeatureId) || string.IsNullOrWhiteSpace(request.ActionId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A feature id and an action id are required."));
        }

        return new CheckActionGrantResponse
        {
            Granted = await _gate.CheckGrantAsync(request.FeatureId, request.ActionId, context.CancellationToken).ConfigureAwait(false),
        };
    }

    public override async Task<NextJobControlResponse> NextJobControl(NextJobControlRequest request, ServerCallContext context)
    {
        var response = new NextJobControlResponse();
        if (await _controls.TakeNextAsync(context.CancellationToken).ConfigureAwait(false) is { } taken)
        {
            response.Control = new JobControl { Id = taken.Id, Kind = taken.Kind, Owner = taken.Owner, Repo = taken.Repo, Action = taken.Action };
            if (taken.Number is { } number)
            {
                response.Control.Number = number;
            }
        }

        return response;
    }

    public override async Task<SettleJobControlResponse> SettleJobControl(SettleJobControlRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Outcome))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A control id and an outcome are required."));
        }

        if (request.Status is not (OwnerControlStatuses.Applied or OwnerControlStatuses.NotApplied or OwnerControlStatuses.NotConfirmed))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The status must be applied, not applied or not confirmed."));
        }

        return new SettleJobControlResponse
        {
            Recorded = await _controls.SettleAsync(request.Id, request.Status, request.Outcome, context.CancellationToken).ConfigureAwait(false),
        };
    }

    public override Task<CommentsView> GetComments(ListCommentsRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => CommentMapping.ToWire(
            await _comments.GetCommentsForWorkerAsync(context.CancellationToken).ConfigureAwait(false)));

    public override Task<CommentsView> SaveComments(CommentsView request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => CommentMapping.ToWire(
            await _comments.SaveCommentsFromWorkerAsync(CommentMapping.ToDomain(request), context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<MediaDirectory> GetVoiceSamplesDirectory(GetVoiceSamplesDirectoryRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => new MediaDirectory
        {
            Path = await _operator.GetVoiceSamplesDirectoryForWorkerAsync(context.CancellationToken).ConfigureAwait(false),
        });

    public override Task<Project> ChatEditSceneText(EditSceneTextRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.EditSceneTextFromChatAsync(request.ProjectId, request.SceneId, request.Text, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ChatRegenerateNarration(SceneRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.RegenerateNarrationFromChatAsync(request.ProjectId, request.SceneId, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ChatRegenerateScene(SceneRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.RegenerateSceneFromChatAsync(request.ProjectId, request.SceneId, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ChatSetSceneLocked(SetSceneLockedRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.SetSceneLockedFromChatAsync(request.ProjectId, request.SceneId, request.Locked, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ChatReorderScenes(ReorderScenesRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.ReorderScenesFromChatAsync(request.ProjectId, request.SceneIds.ToList(), context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ChatProduceProject(ProduceProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.ProduceProjectFromChatAsync(request.Id, request.NarrationVoice, request.Direction, request.Motion, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ChatDelegateProject(DelegateProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.DelegateProjectFromChatAsync(request.Id, request.NarrationVoice, request.Direction, request.Motion, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ChatRequestPresenter(RequestPresenterRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.RequestPresenterFromChatAsync(request.ProjectId, request.Feedback, request.Prompt, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ChatEditScenePicturePrompt(EditScenePicturePromptRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.EditScenePicturePromptFromChatAsync(request.ProjectId, request.SceneId, request.Prompt, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ChatRegenerateScenePicture(SceneRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.RegenerateScenePictureFromChatAsync(request.ProjectId, request.SceneId, context.CancellationToken)
                .ConfigureAwait(false)));
}
