using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over <see cref="FeaturesService"/>. Maps domain
/// exceptions to gRPC status codes; validation failures carry the dotted-path
/// error map in the status detail.
/// </summary>
public sealed class FeaturesGrpcService : GrpcFeatures.FeaturesBase
{
    private readonly FeaturesService _features;

    public FeaturesGrpcService(FeaturesService features)
    {
        _features = features ?? throw new ArgumentNullException(nameof(features));
    }

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
        Guard(async () => FeatureMapping.ToWire(
            await _features.DeleteCredentialsAsync(request.Id, context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Feature> Enable(EnableRequest request, ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            await _features.EnableAsync(request.Id, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Feature> Disable(DisableRequest request, ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            await _features.DisableAsync(request.Id, context.CancellationToken).ConfigureAwait(false)));

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
