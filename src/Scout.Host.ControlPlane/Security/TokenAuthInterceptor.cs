using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Scout.Host.ControlPlane.Security;

/// <summary>
/// Validates the per-session token on every unary and streaming call.
/// The token is read from the <c>authorization</c> metadata header (with an
/// optional <c>Bearer </c> prefix) or the <c>x-scout-token</c> header.
/// Absent or mismatched tokens fail with <see cref="StatusCode.Unauthenticated"/>.
/// </summary>
public sealed class TokenAuthInterceptor : Interceptor
{
    private const string AuthorizationHeader = "authorization";
    private const string ScoutTokenHeader = "x-scout-token";
    private const string BearerPrefix = "Bearer ";

    private readonly SessionToken _token;

    public TokenAuthInterceptor(SessionToken token)
    {
        _token = token ?? throw new ArgumentNullException(nameof(token));
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Authenticate(ServerCallContext context)
    {
        var presented = Extract(context.RequestHeaders);
        if (!_token.Matches(presented))
        {
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "Missing or invalid session token."));
        }
    }

    private static string? Extract(Metadata headers)
    {
        foreach (var entry in headers)
        {
            if (entry.IsBinary)
            {
                continue;
            }

            if (string.Equals(entry.Key, ScoutTokenHeader, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }

            if (string.Equals(entry.Key, AuthorizationHeader, StringComparison.OrdinalIgnoreCase))
            {
                var value = entry.Value;
                return value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                    ? value[BearerPrefix.Length..]
                    : value;
            }
        }

        return null;
    }
}
