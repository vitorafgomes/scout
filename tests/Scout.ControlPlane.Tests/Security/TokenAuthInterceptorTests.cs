using Grpc.Core;
using Scout.Host.ControlPlane.Security;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Security;

public sealed class TokenAuthInterceptorTests
{
    private const string Token = "test-token-value";

    private static ServerCallContext Context(Metadata headers) => new FakeServerCallContext(headers);

    private static TokenAuthInterceptor Interceptor() =>
        new(SessionToken.FromValue(Token));

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

    private sealed class NoopStreamWriter<T> : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => Task.CompletedTask;
    }

    // Minimal ServerCallContext: the interceptor only reads RequestHeaders.
    private sealed class FakeServerCallContext : ServerCallContext
    {
        private readonly Metadata _headers;

        public FakeServerCallContext(Metadata headers)
        {
            _headers = headers;
        }

        protected override string MethodCore => "/test/Method";
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
