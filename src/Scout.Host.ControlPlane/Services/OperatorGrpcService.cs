using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Operator;
using GrpcOperator = Scout.ControlPlane.Contracts.Operator;
using WireMandate = Scout.ControlPlane.Contracts.Mandate;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over <see cref="OperatorService"/> for the desktop token only: the mandate,
/// the projects and the owner's claim decisions.
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
}
