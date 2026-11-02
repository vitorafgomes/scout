using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.WriteGate;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over <see cref="WriteGateService"/>, reachable with the
/// worker token only. A write that cannot be recorded answers
/// <see cref="StatusCode.FailedPrecondition"/>.
/// </summary>
public sealed class WorkerGateGrpcService : WorkerGate.WorkerGateBase
{
    private readonly WriteGateService _gate;

    public WorkerGateGrpcService(WriteGateService gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
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
}
