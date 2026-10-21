using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Providers;
using Contracts = Scout.ControlPlane.Contracts;
using GrpcProviders = Scout.ControlPlane.Contracts.Providers;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over <see cref="ProvidersService"/>. Maps wire ids to
/// domain ids, domain results to wire messages, and domain exceptions to
/// gRPC status codes. Streaming honours the call's cancellation token.
/// </summary>
public sealed class ProvidersGrpcService : GrpcProviders.ProvidersBase
{
    private readonly ProvidersService _providers;

    public ProvidersGrpcService(ProvidersService providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public override async Task<ListProvidersResponse> ListProviders(
        ListProvidersRequest request,
        ServerCallContext context)
    {
        var statuses = await Guard(() => _providers.ListAsync(context.CancellationToken)).ConfigureAwait(false);
        var response = new ListProvidersResponse();
        foreach (var status in statuses)
        {
            response.Providers.Add(ProviderMapping.ToWire(status));
        }
        return response;
    }

    public override Task<Contracts.ProviderStatus> GetProviderStatus(
        GetProviderStatusRequest request,
        ServerCallContext context) =>
        Guard(async () => ProviderMapping.ToWire(
            await _providers.GetStatusAsync(ProviderMapping.ToId(request.Id), context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Contracts.ProviderStatus> StartProvider(
        StartProviderRequest request,
        ServerCallContext context) =>
        Guard(async () => ProviderMapping.ToWire(
            await _providers.StartAsync(ProviderMapping.ToId(request.Id), context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Contracts.ProviderStatus> StopProvider(
        StopProviderRequest request,
        ServerCallContext context) =>
        Guard(async () => ProviderMapping.ToWire(
            await _providers.StopAsync(ProviderMapping.ToId(request.Id), context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<Contracts.ProviderStatus> RestartProvider(
        RestartProviderRequest request,
        ServerCallContext context) =>
        Guard(async () => ProviderMapping.ToWire(
            await _providers.RestartAsync(ProviderMapping.ToId(request.Id), context.CancellationToken)
                .ConfigureAwait(false)));

    public override Task<SetAutostartResponse> SetAutostart(
        SetAutostartRequest request,
        ServerCallContext context) =>
        Guard(async () =>
        {
            await _providers.SetAutostartAsync(
                ProviderMapping.ToId(request.Id),
                request.Enabled,
                context.CancellationToken).ConfigureAwait(false);
            return new SetAutostartResponse();
        });

    public override async Task WatchProviderStatus(
        WatchProviderStatusRequest request,
        IServerStreamWriter<ProviderStatusEvent> responseStream,
        ServerCallContext context)
    {
        var id = ProviderMapping.ToId(request.Id);
        try
        {
            await foreach (var status in _providers
                .WatchAsync(id, context.CancellationToken)
                .ConfigureAwait(false))
            {
                await responseStream
                    .WriteAsync(new ProviderStatusEvent { Status = ProviderMapping.ToWire(status) })
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Client cancelled the watch; end the stream cleanly.
        }
        catch (ProviderUnknownException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
    }

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ProviderUnknownException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (ProviderLifecycleUnsupportedException ex)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
        }
        catch (ProviderNotReadyException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
        }
        catch (ProviderStartTimeoutException ex)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, ex.Message));
        }
        catch (ProviderNotManagedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (ProviderStartFailedException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
        catch (ProviderStopFailedException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}
