using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Providers;
using Shouldly;
using Xunit;
using GrpcModels = Scout.ControlPlane.Contracts.Models;
using GrpcProviders = Scout.ControlPlane.Contracts.Providers;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// Production-readiness boundary failure-injection tests: one per failure mode
/// of the gRPC-over-UDS transport. Every call is bounded by a deadline so a
/// regression that hangs fails fast instead of stalling the suite.
/// </summary>
[Collection("uds")]
public sealed class ControlPlaneBoundaryTests
{
    private static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task BackendNotListening_SurfacesTransportError_NoHang()
    {
        // A UDS path with nothing bound: connect must fail promptly, not hang.
        var dir = Directory.CreateTempSubdirectory("scout-cp-nobackend-").FullName;
        try
        {
            var deadPath = Path.Combine(dir, "absent.sock");
            using var channel = ControlPlaneTestHost.CreateChannelFor(deadPath, ControlPlaneTestHost.Token);
            var client = new GrpcProviders.ProvidersClient(channel);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Should.ThrowAsync<RpcException>(async () =>
                await client.ListProvidersAsync(
                    new ListProvidersRequest(),
                    deadline: DateTime.UtcNow.Add(CallBudget)));
            sw.Stop();

            ex.StatusCode.ShouldBeOneOf(StatusCode.Unavailable, StatusCode.Internal, StatusCode.DeadlineExceeded);
            sw.Elapsed.ShouldBeLessThan(CallBudget + TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HostDisappearsMidStream_StreamTerminatesWithError()
    {
        var host = await ControlPlaneTestHost.StartAsync();
        var channel = ControlPlaneTestHost.CreateChannelFor(host.SocketPath, ControlPlaneTestHost.Token);
        try
        {
            var client = new GrpcModels.ModelsClient(channel);
            using var call = client.PullModel(new PullModelRequest
            {
                Provider = ProviderId.Ollama,
                Model = "llama3",
            });

            // Receive one frame so the stream is genuinely live.
            await host.Ollama.EmitAsync(new PullProgress("downloading", null, 100, 10));
            var first = await ReadNextWithin(call.ResponseStream, CallBudget);
            first.ShouldNotBeNull();

            // Kill the host (and delete the socket) mid-stream.
            await host.DisposeAsync();

            // The remaining read must terminate with an error, not hang forever.
            var ex = await Should.ThrowAsync<RpcException>(async () =>
            {
                using var cts = new CancellationTokenSource(CallBudget);
                while (await call.ResponseStream.MoveNext(cts.Token))
                {
                    // Drain until the broken transport faults.
                }
            });

            ex.StatusCode.ShouldBeOneOf(
                StatusCode.Unavailable, StatusCode.Internal, StatusCode.Cancelled, StatusCode.Unknown);
        }
        finally
        {
            channel.Dispose();
        }
    }

    [Fact]
    public async Task AbsentToken_Unauthenticated_OperationNotExecuted()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateChannel(withToken: false);
        var client = new GrpcModels.ModelsClient(channel);

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await client.DeleteModelAsync(
                new DeleteModelRequest { Provider = ProviderId.Ollama, Model = "llama3" },
                deadline: DateTime.UtcNow.Add(CallBudget)));

        ex.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        // The interceptor rejected before the domain ran: no delete recorded.
        host.Ollama.DeletedModels.ShouldBeEmpty();
    }

    [Fact]
    public async Task BadToken_Unauthenticated_OperationNotExecuted()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = ControlPlaneTestHost.CreateChannelFor(host.SocketPath, "wrong-token");
        var client = new GrpcModels.ModelsClient(channel);

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await client.DeleteModelAsync(
                new DeleteModelRequest { Provider = ProviderId.Ollama, Model = "llama3" },
                deadline: DateTime.UtcNow.Add(CallBudget)));

        ex.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        host.Ollama.DeletedModels.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClientCancelsStreamMidFlight_ServerAbortsUnderlyingWork()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateChannel();
        var client = new GrpcModels.ModelsClient(channel);

        using var call = client.PullModel(new PullModelRequest
        {
            Provider = ProviderId.Ollama,
            Model = "llama3",
        });

        // Receive one frame, then cancel the call without completing the stream.
        await host.Ollama.EmitAsync(new PullProgress("downloading", null, 100, 25));
        var first = await ReadNextWithin(call.ResponseStream, CallBudget);
        first.ShouldNotBeNull();

        call.Dispose(); // cancels the RPC mid-flight

        // The server's PullModel cancellation path must abort the backend pull
        // (observed via the stub's enumerator) rather than running to completion.
        await WaitUntil(() => host.Ollama.PullWasCancelled, CallBudget);
        host.Ollama.PullWasCancelled.ShouldBeTrue();
        host.Ollama.PullCompleted.ShouldBeFalse();
    }

    private static async Task<T?> ReadNextWithin<T>(IAsyncStreamReader<T> stream, TimeSpan budget)
        where T : class
    {
        using var cts = new CancellationTokenSource(budget);
        return await stream.MoveNext(cts.Token) ? stream.Current : null;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow.Add(budget);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
    }
}
