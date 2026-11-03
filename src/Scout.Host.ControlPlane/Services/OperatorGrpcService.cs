using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Operator;
using GrpcOperator = Scout.ControlPlane.Contracts.Operator;
using WireMandate = Scout.ControlPlane.Contracts.Mandate;
using WireReadiness = Scout.ControlPlane.Contracts.Readiness;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over <see cref="OperatorService"/> for the desktop token only: the mandate,
/// the projects, the owner's claim decisions, production, the scene edits, the media directory,
/// the worker's readiness, assembly, the owner's package metadata, media rights and approval, publication,
/// proposal decisions and experiments.
/// </summary>
public sealed class OperatorGrpcService : GrpcOperator.OperatorBase
{
    private readonly OperatorService _operator;

    public OperatorGrpcService(OperatorService @operator)
    {
        _operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
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
            await _operator.StartProjectAsync(request.Topic, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Project> DecideClaim(DecideClaimRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.DecideClaimAsync(
                    request.ProjectId,
                    request.ClaimId,
                    request.Status,
                    request.HasNote ? request.Note : null,
                    context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Project> ProduceProject(GetProjectRequest request, ServerCallContext context) =>
        OperatorMapping.Guard(async () => OperatorMapping.ToWire(
            await _operator.ProduceProjectAsync(request.Id, context.CancellationToken).ConfigureAwait(false)));

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
}
