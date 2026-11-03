using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Host.ControlPlane.Security;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Security;

public sealed class TokenAuthInterceptorTests
{
    private const string Token = "test-token-value";
    private const string WorkerToken = "test-worker-token-value";
    private const string ListFeatures = "/scout.controlplane.v1.Features/ListFeatures";
    private const string ApproveAction = "/scout.controlplane.v1.Features/ApproveAction";
    private const string StartProvider = "/scout.controlplane.v1.Providers/StartProvider";
    private const string PullModel = "/scout.controlplane.v1.Models/PullModel";
    private const string RequestWrite = "/scout.controlplane.v1.WorkerGate/RequestWrite";
    private const string RecordWrite = "/scout.controlplane.v1.WorkerGate/RecordWrite";

    private static ServerCallContext Context(Metadata headers, string method = ListFeatures) =>
        new FakeServerCallContext(headers, method);

    private static TokenAuthInterceptor Interceptor(string? workerToken = WorkerToken) =>
        new(CallerTokens.Create(SessionToken.FromValue(Token), workerToken, NullLogger<CallerTokens>.Instance));

    private static Task<string> Call(string token, string method, string? workerToken = WorkerToken) =>
        Interceptor(workerToken).UnaryServerHandler<string, string>(
            "req",
            Context(new Metadata { { "x-scout-token", token } }, method),
            (_, _) => Task.FromResult("ok"));

    [Theory]
    [InlineData(ListFeatures)]
    [InlineData(ApproveAction)]
    [InlineData(StartProvider)]
    [InlineData(PullModel)]
    public async Task The_Desktop_Token_Reaches_Features_Providers_And_Models(string method)
    {
        (await Call(Token, method)).ShouldBe("ok");
    }

    [Theory]
    [InlineData(RequestWrite)]
    [InlineData(RecordWrite)]
    public async Task The_Worker_Token_Reaches_The_Worker_Gate(string method)
    {
        (await Call(WorkerToken, method)).ShouldBe("ok");
    }

    [Theory]
    [InlineData(WorkerToken, ListFeatures)]
    [InlineData(WorkerToken, ApproveAction)]
    [InlineData(WorkerToken, StartProvider)]
    [InlineData(WorkerToken, PullModel)]
    [InlineData(Token, RequestWrite)]
    [InlineData(Token, RecordWrite)]
    [InlineData(Token, "/grpc.reflection.v1.ServerReflection/ServerReflectionInfo")]
    [InlineData(Token, "/scout.controlplane.v1.FeaturesX/ListFeatures")]
    [InlineData(WorkerToken, "/scout.controlplane.v1.WorkerGateX/RequestWrite")]
    public async Task A_Caller_Outside_Its_Role_Is_Denied_Naming_The_Method(string token, string method)
    {
        var ex = await Should.ThrowAsync<RpcException>(() => Call(token, method));

        ex.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        ex.Status.Detail.ShouldBe($"This caller may not call {method}.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Without_A_Worker_Token_The_Worker_Gate_Is_Unreachable_For_Everyone(string? workerToken)
    {
        var worker = await Should.ThrowAsync<RpcException>(() => Call(WorkerToken, RequestWrite, workerToken));
        var empty = await Should.ThrowAsync<RpcException>(() => Call(string.Empty, RequestWrite, workerToken));
        var desktop = await Should.ThrowAsync<RpcException>(() => Call(Token, RequestWrite, workerToken));

        worker.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        empty.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        desktop.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        (await Call(Token, ListFeatures, workerToken)).ShouldBe("ok");
    }

    [Fact]
    public async Task ValidTokenInAuthorizationHeader_PassesThrough()
    {
        var headers = new Metadata { { "authorization", $"Bearer {Token}" } };
        var called = false;

        await Interceptor().UnaryServerHandler<string, string>(
            "req",
            Context(headers),
            (_, _) => { called = true; return Task.FromResult("ok"); });

        called.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidTokenInScoutHeader_PassesThrough()
    {
        var headers = new Metadata { { "x-scout-token", Token } };
        var result = await Interceptor().UnaryServerHandler<string, string>(
            "req",
            Context(headers),
            (_, _) => Task.FromResult("ok"));

        result.ShouldBe("ok");
    }

    [Fact]
    public async Task InvalidToken_ThrowsUnauthenticated()
    {
        var headers = new Metadata { { "x-scout-token", "wrong" } };

        var ex = await Should.ThrowAsync<RpcException>(() =>
            Interceptor().UnaryServerHandler<string, string>(
                "req",
                Context(headers),
                (_, _) => Task.FromResult("ok")));

        ex.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task AbsentToken_ThrowsUnauthenticated()
    {
        var ex = await Should.ThrowAsync<RpcException>(() =>
            Interceptor().UnaryServerHandler<string, string>(
                "req",
                Context(new Metadata()),
                (_, _) => Task.FromResult("ok")));

        ex.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task InvalidToken_OnServerStream_ThrowsUnauthenticated()
    {
        var headers = new Metadata { { "x-scout-token", "wrong" } };

        var ex = await Should.ThrowAsync<RpcException>(() =>
            Interceptor().ServerStreamingServerHandler<string, string>(
                "req",
                new NoopStreamWriter<string>(),
                Context(headers),
                (_, _, _) => Task.CompletedTask));

        ex.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task The_Worker_Token_On_A_Server_Stream_Is_Denied()
    {
        var headers = new Metadata { { "x-scout-token", WorkerToken } };

        var ex = await Should.ThrowAsync<RpcException>(() =>
            Interceptor().ServerStreamingServerHandler<string, string>(
                "req",
                new NoopStreamWriter<string>(),
                Context(headers, PullModel),
                (_, _, _) => Task.CompletedTask));

        ex.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Theory]
    [InlineData("/scout.controlplane.v1.Operator/GetMandate")]
    [InlineData("/scout.controlplane.v1.Operator/SaveMandate")]
    [InlineData("/scout.controlplane.v1.Operator/ListProjects")]
    [InlineData("/scout.controlplane.v1.Operator/GetProject")]
    [InlineData("/scout.controlplane.v1.Operator/StartProject")]
    [InlineData("/scout.controlplane.v1.Operator/DecideClaim")]
    public async Task The_Operator_Service_Belongs_To_The_Desktop_Alone(string method)
    {
        var worker = await Should.ThrowAsync<RpcException>(() => Call(WorkerToken, method));

        (await Call(Token, method)).ShouldBe("ok");
        worker.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        worker.Status.Detail.ShouldBe($"This caller may not call {method}.");
    }

    [Theory]
    [InlineData("/scout.controlplane.v1.WorkerGate/GetOperatorMandate")]
    [InlineData("/scout.controlplane.v1.WorkerGate/SaveOperatorProject")]
    [InlineData("/scout.controlplane.v1.WorkerGate/NextQueuedProject")]
    [InlineData("/scout.controlplane.v1.WorkerGate/GetOperatorProject")]
    [InlineData("/scout.controlplane.v1.WorkerGate/ListOperatorProjects")]
    public async Task The_Workers_Operator_Rpcs_Belong_To_The_Worker_Alone(string method)
    {
        var desktop = await Should.ThrowAsync<RpcException>(() => Call(Token, method));

        (await Call(WorkerToken, method)).ShouldBe("ok");
        desktop.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        desktop.Status.Detail.ShouldBe($"This caller may not call {method}.");
    }

    private sealed class NoopStreamWriter<T> : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => Task.CompletedTask;
    }

    private sealed class FakeServerCallContext : ServerCallContext
    {
        private readonly Metadata _headers;
        private readonly string _method;

        public FakeServerCallContext(Metadata headers, string method)
        {
            _headers = headers;
            _method = method;
        }

        protected override string MethodCore => _method;
        protected override string HostCore => string.Empty;
        protected override string PeerCore => "uds:test";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
        protected override Metadata RequestHeadersCore => _headers;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore { get; } = new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(string.Empty, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }
}
