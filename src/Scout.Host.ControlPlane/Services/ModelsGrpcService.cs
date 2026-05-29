using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Providers;
using GrpcModels = Scout.ControlPlane.Contracts.Models;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over the provider model operations. PullModel maps the
/// domain <c>IAsyncEnumerable&lt;PullProgress&gt;</c> onto a server stream,
/// honouring the call's cancellation token.
/// </summary>
public sealed class ModelsGrpcService : GrpcModels.ModelsBase
{
    private readonly ProvidersService _providers;

    public ModelsGrpcService(ProvidersService providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public override async Task PullModel(
        PullModelRequest request,
        IServerStreamWriter<ModelPullProgress> responseStream,
        ServerCallContext context)
    {
        var id = ProviderMapping.ToId(request.Provider);
        try
        {
            await foreach (var progress in _providers
                .PullModelAsync(id, request.Model, context.CancellationToken)
                .ConfigureAwait(false))
            {
                await responseStream.WriteAsync(ToWire(progress)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Client cancelled the pull; end the stream cleanly.
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
        catch (HttpRequestException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"Model pull failed: {ex.Message}"));
        }
    }

    public override async Task<DeleteModelResponse> DeleteModel(
        DeleteModelRequest request,
        ServerCallContext context)
    {
        var id = ProviderMapping.ToId(request.Provider);
        try
        {
            await _providers.DeleteModelAsync(id, request.Model, context.CancellationToken)
                .ConfigureAwait(false);
            return new DeleteModelResponse { Model = request.Model };
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
        catch (HttpRequestException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"Model delete failed: {ex.Message}"));
        }
    }

    public override async Task<ListModelsResponse> ListModels(
        ListModelsRequest request,
        ServerCallContext context)
    {
        var id = ProviderMapping.ToId(request.Provider);
        try
        {
            var models = await _providers.ListModelsAsync(id, context.CancellationToken)
                .ConfigureAwait(false);
            var response = new ListModelsResponse();
            foreach (var model in models)
            {
                response.Models.Add(ProviderMapping.ToWire(model));
            }
            return response;
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
    }

    private static ModelPullProgress ToWire(PullProgress progress)
    {
        var percent = 0d;
        if (progress.Total is { } total && total > 0 && progress.Completed is { } completed)
        {
            percent = Math.Clamp(completed * 100d / total, 0d, 100d);
        }

        return new ModelPullProgress
        {
            Status = progress.Status,
            Percent = percent,
            CompletedBytes = progress.Completed ?? 0,
            TotalBytes = progress.Total ?? 0,
        };
    }
}
