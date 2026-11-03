using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using WireMandate = Scout.ControlPlane.Contracts.Mandate;
using WireReadiness = Scout.ControlPlane.Contracts.Readiness;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over <see cref="WriteGateService"/> and the worker side of
/// <see cref="OperatorService"/>, reachable with the worker token only. A write that cannot be
/// recorded answers <see cref="StatusCode.FailedPrecondition"/>.
/// </summary>
public sealed class WorkerGateGrpcService : WorkerGate.WorkerGateBase
{
    private readonly WriteGateService _gate;
    private readonly OperatorService _operator;

    public WorkerGateGrpcService(WriteGateService gate, OperatorService @operator)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
    }

    public override async Task<RequestWriteResponse> RequestWrite(
        RequestWriteRequest request,
        ServerCallContext context)
    {
        var decision = await _gate.RequestAsync(
                request.FeatureId,
                request.Scope,
                request.Target,
                request.Summary,
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
}
