using Grpc.Core;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;
using WirePendingAction = Scout.ControlPlane.Contracts.PendingAction;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over <see cref="FeaturesService"/>, the action queue and
/// the write log. Deleting the YouTube credentials first purges what Scout read from YouTube through
/// <see cref="OperatorService.PurgeYouTubeDataAsync"/>; a purge that fails keeps the credentials. Revoking the
/// YouTube write grant, disabling YouTube or deleting its credentials first rejects every approved YouTube action through
/// <see cref="OperatorService.CancelApprovedYouTubeActionsAsync"/>. Maps domain
/// exceptions to gRPC status codes; validation failures carry the dotted-path
/// error map in the status detail.
/// </summary>
public sealed class FeaturesGrpcService(FeaturesService features, IActionQueue actions, IWriteLog writes, OperatorService @operator) : GrpcFeatures.FeaturesBase
{
    private const int DefaultWritesLimit = 50;

    private readonly FeaturesService _features = features ?? throw new ArgumentNullException(nameof(features));
    private readonly IActionQueue _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    private readonly IWriteLog _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    private readonly OperatorService _operator = @operator ?? throw new ArgumentNullException(nameof(@operator));

    public override async Task<ListFeaturesResponse> ListFeatures(
        ListFeaturesRequest request,
        ServerCallContext context)
    {
        var snapshots = await Guard(() => _features.ListAsync(context.CancellationToken))
            .ConfigureAwait(false);
        var response = new ListFeaturesResponse();
        foreach (var snapshot in snapshots)
        {
            response.Features.Add(FeatureMapping.ToWire(snapshot));
        }
        return response;
    }

    public override Task<Feature> SetCredentials(
        SetCredentialsRequest request,
        ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            await _features.SetCredentialsAsync(
                request.Id,
                request.Credentials,
                context.CancellationToken).ConfigureAwait(false)));

    public override Task<Feature> Setup(SetupRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (key, value) in request.Settings)
            {
                settings[key] = value;
            }
            foreach (var cleared in request.ClearedSettings)
            {
                settings[cleared] = null;
            }

            var input = new FeatureSetupInput(
                Credentials: request.Credentials.Count == 0 ? null : request.Credentials,
                Settings: settings.Count == 0 ? null : settings);

            return FeatureMapping.ToWire(
                await _features.SetupAsync(request.Id, input, context.CancellationToken)
                    .ConfigureAwait(false));
        });

    public override Task<Feature> DeleteCredentials(
        DeleteCredentialsRequest request,
        ServerCallContext context) =>
        Guard(async () =>
        {
            if (request.Id == FeatureCatalog.YouTubeId)
            {
                await _operator.PurgeYouTubeDataAsync(context.CancellationToken).ConfigureAwait(false);
                await _operator.CancelApprovedYouTubeActionsAsync(context.CancellationToken).ConfigureAwait(false);
            }

            return FeatureMapping.ToWire(
                await _features.DeleteCredentialsAsync(request.Id, context.CancellationToken).ConfigureAwait(false));
        });

    public override Task<Feature> Enable(EnableRequest request, ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            await _features.EnableAsync(request.Id, request.ConfirmWrites, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Feature> Disable(DisableRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            if (request.Id == FeatureCatalog.YouTubeId)
            {
                await _operator.CancelApprovedYouTubeActionsAsync(context.CancellationToken).ConfigureAwait(false);
            }

            return FeatureMapping.ToWire(
                await _features.DisableAsync(request.Id, context.CancellationToken).ConfigureAwait(false));
        });

    public override Task<Feature> SetWriteGrant(SetWriteGrantRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            if (!request.Granted && request.Id == FeatureCatalog.YouTubeId)
            {
                await _operator.CancelApprovedYouTubeActionsAsync(context.CancellationToken).ConfigureAwait(false);
            }

            return FeatureMapping.ToWire(
                await _features.SetWriteGrantAsync(
                    request.Id,
                    request.Granted,
                    request.ConfirmWrites,
                    context.CancellationToken).ConfigureAwait(false));
        });

    public override Task<ListPendingActionsResponse> ListPendingActions(
        ListPendingActionsRequest request,
        ServerCallContext context) =>
        Guard(async () =>
        {
            var response = new ListPendingActionsResponse();
            foreach (var action in await _actions.ListPendingAsync(context.CancellationToken).ConfigureAwait(false))
            {
                response.Actions.Add(FeatureMapping.ToWire(action));
            }
            return response;
        });

    public override Task<WirePendingAction> ApproveAction(
        ActionDecisionRequest request,
        ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            await _actions.ApproveAsync(
                request.Id,
                request.HasReason ? request.Reason : null,
                context.CancellationToken).ConfigureAwait(false)));

    public override Task<WirePendingAction> RejectAction(
        ActionDecisionRequest request,
        ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            await _actions.RejectAsync(
                request.Id,
                request.HasReason ? request.Reason : null,
                context.CancellationToken).ConfigureAwait(false)));

    public override Task<ListWritesResponse> ListWrites(ListWritesRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            var limit = request.Limit > 0 ? request.Limit : DefaultWritesLimit;
            var response = new ListWritesResponse();
            foreach (var entry in await _writes.ListAsync(limit, context.CancellationToken).ConfigureAwait(false))
            {
                response.Entries.Add(FeatureMapping.ToWire(entry));
            }
            return response;
        });

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (FeatureUnknownException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (FeatureValidationFailedException ex)
        {
            var detail = string.Join("; ", ex.Errors.Select(e => $"{e.Key}: {e.Value}"));
            throw new RpcException(new Status(StatusCode.InvalidArgument, detail));
        }
        catch (FeatureCredentialsMissingException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (WriteConfirmationRequiredException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (PendingActionNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (PendingActionAlreadyDecidedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (FeatureNotImplementedException ex)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
        }
        catch (FeatureActivationException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
        catch (FeaturePersistFailedException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}
