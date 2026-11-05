using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Operator;
using GrpcOperator = Scout.ControlPlane.Contracts.Operator;
using WireMandate = Scout.ControlPlane.Contracts.Mandate;
using WireReadiness = Scout.ControlPlane.Contracts.Readiness;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over <see cref="OperatorService"/> for the desktop token only: the mandate,
/// the projects, the owner's claim decisions, production, delegation, the scene edits, the scene text and picture prompt,
/// the presenter picture's mouth box and its regeneration, the presenter requests and versions, Scout's own presenters,
/// the media directory, project deletion,
/// the worker's readiness, assembly, the owner's package metadata, media rights and approval, publication,
/// proposal decisions and experiments, and the comments: the list, a sync request, the owner's decisions, approved
/// replies and ideas.
/// </summary>
public sealed class OperatorGrpcService : GrpcOperator.OperatorBase
{
    private readonly OperatorService _operator;
    private readonly CommentService _comments;

    public OperatorGrpcService(OperatorService @operator, CommentService comments)
    {
        _operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
        _comments = comments ?? throw new ArgumentNullException(nameof(comments));
    }

    public override Task<WireMandate> GetMandate(GetMandateRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () =>
            OperatorMapping.ToWire(await _operator.GetMandateAsync(context.CancellationToken).ConfigureAwait(false)));

    public override Task<WireMandate> SaveMandate(WireMandate request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.SaveMandateAsync(OperatorMapping.ToDomain(request), context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<ListProjectsResponse> ListProjects(ListProjectsRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () =>
        {
            var projects = await _operator.ListProjectsAsync(request.Limit, context.CancellationToken)
                .ConfigureAwait(false);
            var response = new ListProjectsResponse();
            response.Projects.AddRange(projects.Select(OperatorMapping.ToWire));
            return response;
        });

    public override Task<Project> GetProject(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.GetProjectAsync(request.Id, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> StartProject(StartProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.StartProjectAsync(request.Topic, request.Format, request.Direction, request.Presenter, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> DecideClaim(DecideClaimRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.DecideClaimAsync(
                    request.ProjectId,
                    request.ClaimId,
                    request.Status,
                    request.HasNote ? request.Note : null,
                    context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ProduceProject(ProduceProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.ProduceProjectAsync(request.Id, request.NarrationVoice, request.Direction, request.Motion, request.Presenter, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> DelegateProject(DelegateProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.DelegateProjectAsync(request.Id, request.NarrationVoice, request.Direction, request.Motion, request.Presenter, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> ReorderScenes(ReorderScenesRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.ReorderScenesAsync(request.ProjectId, request.SceneIds.ToList(), context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> SetSceneLocked(SetSceneLockedRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.SetSceneLockedAsync(request.ProjectId, request.SceneId, request.Locked, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> RegenerateScene(SceneRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.RegenerateSceneAsync(request.ProjectId, request.SceneId, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> EditSceneText(EditSceneTextRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.EditSceneTextAsync(request.ProjectId, request.SceneId, request.Text, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> RegenerateNarration(SceneRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.RegenerateNarrationAsync(request.ProjectId, request.SceneId, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> SetMouthBox(SetMouthBoxRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.SetMouthBoxAsync(request.ProjectId, OperatorMapping.ToDomain(request.MouthBox), context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> RegeneratePresenter(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.RegeneratePresenterAsync(request.Id, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> RequestPresenter(RequestPresenterRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.RequestPresenterAsync(request.ProjectId, request.Feedback, request.Prompt, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> UsePresenterVersion(UsePresenterVersionRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.UsePresenterVersionAsync(request.ProjectId, request.Index, context.CancellationToken).ConfigureAwait(false)));

    public override Task<BuiltInPresenters> ListBuiltInPresenters(ListBuiltInPresentersRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () =>
        {
            var response = new BuiltInPresenters();
            response.Presenters.AddRange((await _operator.ListBuiltInPresentersAsync(context.CancellationToken).ConfigureAwait(false))
                .Select(OperatorMapping.ToWire));
            return response;
        });

    public override Task<Project> EditScenePicturePrompt(EditScenePicturePromptRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.EditScenePicturePromptAsync(request.ProjectId, request.SceneId, request.Prompt, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> RegenerateScenePicture(SceneRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.RegenerateScenePictureAsync(request.ProjectId, request.SceneId, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> AttachSceneMedia(AttachSceneMediaRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.AttachSceneMediaAsync(
                    request.ProjectId,
                    request.SceneId,
                    request.Kind,
                    request.FileName,
                    context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<MediaDirectory> GetMediaDirectory(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => new MediaDirectory
        {
            Path = await _operator.GetMediaDirectoryAsync(request.Id, context.CancellationToken).ConfigureAwait(false),
        });

    public override Task<DeleteProjectResponse> DeleteProject(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () =>
        {
            await _operator.DeleteProjectAsync(request.Id, context.CancellationToken).ConfigureAwait(false);
            return new DeleteProjectResponse();
        });

    public override Task<WireReadiness> GetReadiness(GetReadinessRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () =>
            OperatorMapping.ToWire(await _operator.GetReadinessAsync(context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> AssembleProject(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.AssembleProjectAsync(request.Id, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> ConfirmMediaRights(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.ConfirmMediaRightsAsync(request.Id, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> EditPackageMetadata(EditPackageMetadataRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.EditPackageMetadataAsync(
                    request.ProjectId,
                    request.Title,
                    request.Description,
                    request.Tags.ToList(),
                    context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ApprovePackage(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.ApprovePackageAsync(request.Id, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> PublishPackage(PublishPackageRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.PublishPackageAsync(
                    request.ProjectId,
                    request.PrivacyStatus,
                    OperatorMapping.ToDomainTime(request.HasPublishAt, request.PublishAt),
                    request.MadeForKids,
                    request.SyntheticMedia,
                    context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> DecideProposal(DecideProposalRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.DecideProposalAsync(request.ProjectId, request.ProposalId, request.Accept, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> StartExperiment(StartExperimentRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.StartExperimentAsync(request.ProjectId, request.ArmHours, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> AdoptExperimentWinner(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.AdoptExperimentWinnerAsync(request.Id, context.CancellationToken).ConfigureAwait(false)));

    public override Task<CommentsView> ListComments(ListCommentsRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => CommentMapping.ToWire(
            await _comments.ListCommentsAsync(context.CancellationToken).ConfigureAwait(false)));

    public override Task<CommentsView> RequestCommentSync(ListCommentsRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => CommentMapping.ToWire(
            await _comments.RequestCommentSyncAsync(context.CancellationToken).ConfigureAwait(false)));

    public override Task<CommentsView> DecideComment(DecideCommentRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => CommentMapping.ToWire(
            await _comments.DecideCommentAsync(request.CommentId, request.Decision, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<CommentsView> ApproveReply(ApproveReplyRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => CommentMapping.ToWire(
            await _comments.ApproveReplyAsync(request.CommentId, request.Text, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<CommentsView> DecideIdea(DecideIdeaRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => CommentMapping.ToWire(
            await _comments.DecideIdeaAsync(request.IdeaId, request.Use, context.CancellationToken).ConfigureAwait(false)));

    public override Task<MediaDirectory> GetVoiceSamplesDirectory(GetVoiceSamplesDirectoryRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => new MediaDirectory
        {
            Path = await _operator.GetVoiceSamplesDirectoryAsync(context.CancellationToken).ConfigureAwait(false),
        });
}
